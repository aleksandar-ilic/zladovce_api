using System.Net;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zladovce.Application;
using Zladovce.Application.Media;
using Zladovce.Infrastructure;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Zladovce.Lambda;

/// <summary>
/// Image processor Lambda, triggered by S3 <c>ObjectCreated</c> events for the <c>uploads/</c> prefix of the media bucket.
/// Handler string: <c>Zladovce.Lambda::Zladovce.Lambda.ImageProcessorFunction::HandleAsync</c>.
/// For every upload it verifies the file is a JPEG/PNG/WebP image, strips metadata, applies the orientation, writes WebP
/// renditions (400/800/1600 px, never enlarged) and a manifest under <c>media/…/{imageId}/</c> and deletes the upload.
/// It never touches the database; the API attaches the image when the administrator confirms it.
/// Configuration: <c>Media__BucketName</c> (required), <c>Media__BaseUrl</c>, optional <c>Media__Region</c>.
/// </summary>
public sealed class ImageProcessorFunction
{
    private readonly IServiceProvider _services;

    /// <summary>Used by the Lambda runtime.</summary>
    public ImageProcessorFunction()
        : this(new ConfigurationBuilder().AddEnvironmentVariables().Build())
    {
    }

    /// <summary>Used by tests and local runs with explicit configuration.</summary>
    public ImageProcessorFunction(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("Amazon", LogLevel.Warning)
            .AddJsonConsole());
        services.AddMediaStorage(configuration);
        services.AddImageProcessing();
        _services = services.BuildServiceProvider();
    }

    public async Task HandleAsync(S3Event s3Event, ILambdaContext context)
    {
        var processor = _services.GetRequiredService<IImageProcessor>();
        var bucket = _services.GetRequiredService<IOptions<MediaOptions>>().Value.BucketName;
        var logger = _services.GetRequiredService<ILogger<ImageProcessorFunction>>();

        using var timeout = new CancellationTokenSource();
        if (context.RemainingTime > TimeSpan.FromSeconds(5))
        {
            timeout.CancelAfter(context.RemainingTime - TimeSpan.FromSeconds(2));
        }

        foreach (var record in s3Event.Records ?? [])
        {
            var recordBucket = record.S3?.Bucket?.Name;
            var rawKey = record.S3?.Object?.Key;
            if (string.IsNullOrEmpty(rawKey))
            {
                continue;
            }

            if (!string.Equals(recordBucket, bucket, StringComparison.Ordinal))
            {
                logger.LogWarning("Ignoring an event for another bucket.");
                continue;
            }

            // Keys in S3 event notifications are URL-encoded (spaces as '+').
            var key = WebUtility.UrlDecode(rawKey);
            if (!key.StartsWith(MediaKeys.UploadsPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // Exceptions propagate: the asynchronous invocation is retried by Lambda. Processing is idempotent.
            var result = await processor.ProcessUploadAsync(key, timeout.Token);
            logger.LogInformation("Upload {UploadKey}: {Status}.", key, result.Status);
        }
    }
}
