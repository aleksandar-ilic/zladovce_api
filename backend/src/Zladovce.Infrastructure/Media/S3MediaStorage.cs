using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using Zladovce.Application.Media;

namespace Zladovce.Infrastructure.Media;

/// <summary>Builds the S3 client from <see cref="MediaOptions"/>: Amazon S3 by default, an S3-compatible endpoint when <c>Media:ServiceUrl</c> is set.</summary>
public static class S3ClientFactory
{
    public static IAmazonS3 Create(MediaOptions options)
    {
        var config = new AmazonS3Config();

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            config.ForcePathStyle = options.ForcePathStyle;
            config.AuthenticationRegion = string.IsNullOrWhiteSpace(options.Region) ? "us-east-1" : options.Region;

            // S3-compatible stores do not all implement the newer default integrity checksums.
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        else if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        // Static keys are for a local store only; on AWS the execution role supplies credentials.
        return !string.IsNullOrWhiteSpace(options.AccessKey) && !string.IsNullOrWhiteSpace(options.SecretKey)
            ? new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
            : new AmazonS3Client(config);
    }
}

/// <summary><see cref="IMediaStorage"/> on Amazon S3 or an S3-compatible store.</summary>
public sealed class S3MediaStorage : IMediaStorage, IDisposable
{
    private readonly Lazy<IAmazonS3> _s3;
    private readonly IOptions<MediaOptions> _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Used by dependency injection: the client is built from <see cref="MediaOptions"/> on first use.</summary>
    public S3MediaStorage(IOptions<MediaOptions> options, TimeProvider timeProvider)
        : this(new Lazy<IAmazonS3>(() => S3ClientFactory.Create(options.Value)), options, timeProvider)
    {
    }

    internal S3MediaStorage(Lazy<IAmazonS3> s3, IOptions<MediaOptions> options, TimeProvider timeProvider)
    {
        _s3 = s3;
        _options = options;
        _timeProvider = timeProvider;
    }

    private IAmazonS3 S3 => _s3.Value;

    public void Dispose()
    {
        if (_s3.IsValueCreated)
        {
            _s3.Value.Dispose();
        }
    }

    private string Bucket => !string.IsNullOrWhiteSpace(_options.Value.BucketName)
        ? _options.Value.BucketName
        : throw new InvalidOperationException("Media storage is not configured: set Media:BucketName.");

    public async Task<PresignedUpload> CreatePresignedUploadAsync(
        string key, string contentType, long maxBytes, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var expiresAt = _timeProvider.GetUtcNow() + lifetime;

        var request = new CreatePresignedPostRequest
        {
            BucketName = Bucket,
            Key = key,
            Expires = expiresAt.UtcDateTime,
        };

        // The form must send exactly this content type, and the file must fit the size limit;
        // the storage itself refuses anything else.
        request.Fields["Content-Type"] = contentType;
        request.Conditions.Add(S3PostCondition.ExactMatch("Content-Type", contentType));
        request.Conditions.Add(S3PostCondition.ContentLengthRange(1, maxBytes));

        var response = await S3.CreatePresignedPostAsync(request);

        return new PresignedUpload(response.Url, new Dictionary<string, string>(response.Fields, StringComparer.Ordinal), expiresAt);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await S3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = Bucket, Key = key }, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<MediaObject?> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await S3.GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = key }, cancellationToken);
            if (response.ContentLength > maxBytes)
            {
                throw new MediaObjectTooLargeException(key);
            }

            using var buffer = new MemoryStream(capacity: (int)Math.Max(response.ContentLength, 0));
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > maxBytes)
            {
                throw new MediaObjectTooLargeException(key);
            }

            return new MediaObject(buffer.ToArray(), response.Headers.ContentType);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task WriteAsync(string key, ReadOnlyMemory<byte> content, string contentType, string? cacheControl, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        var request = new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false,
        };

        if (!string.IsNullOrEmpty(cacheControl))
        {
            request.Headers.CacheControl = cacheControl;
        }

        await S3.PutObjectAsync(request, cancellationToken);
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        // One request per object: a handful of keys at most, and no need for the multi-delete permission shape.
        foreach (var key in keys)
        {
            await S3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = key }, cancellationToken);
        }
    }
}
