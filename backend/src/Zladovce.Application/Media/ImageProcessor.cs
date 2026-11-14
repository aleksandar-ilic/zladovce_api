using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zladovce.Application.Common.Errors;

namespace Zladovce.Application.Media;

/// <summary>A pre-signed browser upload (HTML form POST) to the media bucket.</summary>
/// <param name="Url">Where to POST the multipart form.</param>
/// <param name="Fields">Form fields that must precede the file, exactly as given.</param>
/// <param name="ExpiresAt">After this moment the storage refuses the upload.</param>
public sealed record PresignedUpload(string Url, IReadOnlyDictionary<string, string> Fields, DateTimeOffset ExpiresAt);

/// <summary>An object read from the media bucket.</summary>
public sealed record MediaObject(byte[] Content, string? ContentType);

/// <summary>Thrown by <see cref="IMediaStorage.ReadAsync"/> when the object is larger than the caller accepts.</summary>
public sealed class MediaObjectTooLargeException(string key) : Exception($"The object '{key}' is larger than allowed.");

/// <summary>The media bucket (Amazon S3, or an S3-compatible store in local development).</summary>
public interface IMediaStorage
{
    /// <summary>
    /// Creates a pre-signed POST that accepts exactly one object: this key, this content type and at most
    /// <paramref name="maxBytes"/> bytes.
    /// </summary>
    Task<PresignedUpload> CreatePresignedUploadAsync(string key, string contentType, long maxBytes, TimeSpan lifetime, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    /// <summary>Returns null when the object does not exist.</summary>
    Task<MediaObject?> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken);

    Task WriteAsync(string key, ReadOnlyMemory<byte> content, string contentType, string? cacheControl, CancellationToken cancellationToken);

    /// <summary>Deletes the objects; missing ones are ignored.</summary>
    Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}

/// <summary>Thrown by <see cref="IImageTransformer"/> when the bytes are not an acceptable image.</summary>
public sealed class InvalidImageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One encoded rendition.</summary>
public sealed record ImageRendition(int Width, byte[] Content);

/// <summary>Result of re-encoding an image.</summary>
/// <param name="Width">Width of the original after its orientation was applied.</param>
/// <param name="Height">Height of the original after its orientation was applied.</param>
public sealed record TransformedImage(int Width, int Height, IReadOnlyList<ImageRendition> Renditions);

/// <summary>
/// Decodes an untrusted upload and re-encodes it: verifies that it really is a JPEG, PNG or WebP image,
/// applies the orientation, removes metadata and produces the WebP renditions of <see cref="RenditionPlan"/>.
/// </summary>
public interface IImageTransformer
{
    /// <exception cref="InvalidImageException">The content is not an acceptable image.</exception>
    TransformedImage Transform(byte[] source);
}

/// <summary>A processed image as stored under <c>media/</c>.</summary>
/// <param name="StorageKey">Base key of the renditions.</param>
/// <param name="Width">Width of the original in pixels.</param>
/// <param name="Height">Height of the original in pixels.</param>
/// <param name="Widths">Widths of the renditions that exist: <c>{StorageKey}/{width}.webp</c>.</param>
public sealed record ProcessedImage(string StorageKey, int Width, int Height, IReadOnlyList<int> Widths);

public enum ImageProcessingStatus
{
    /// <summary>Renditions and manifest are in place; the upload was deleted.</summary>
    Processed,

    /// <summary>The upload is not an acceptable image; it was deleted and a rejection manifest was written.</summary>
    Rejected,

    /// <summary>There is no such upload (never uploaded, already processed, or expired).</summary>
    UploadMissing,

    /// <summary>The key is not an upload key of this application; nothing was touched.</summary>
    NotAnUploadKey,
}

public sealed record ImageProcessingResult(ImageProcessingStatus Status, ProcessedImage? Image = null);

/// <summary>
/// Turns raw uploads into public renditions. The same code runs in the image processor Lambda (S3 event)
/// and, when <see cref="MediaOptions.ProcessInline"/> is on, inside the API. It needs the bucket only, never the database.
/// </summary>
public interface IImageProcessor
{
    /// <summary>Processes one upload. Safe to repeat: an already processed upload reports its stored result.</summary>
    Task<ImageProcessingResult> ProcessUploadAsync(string uploadKey, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the processed image behind an upload key, processing it inline or waiting for the processor as configured.
    /// </summary>
    /// <exception cref="RequestValidationException">The key is not an upload key for <paramref name="purpose"/>.</exception>
    /// <exception cref="BadRequestException"><c>INVALID_IMAGE</c> or <c>UPLOAD_NOT_FOUND</c>.</exception>
    /// <exception cref="ConflictException"><c>IMAGE_NOT_READY</c>.</exception>
    Task<ProcessedImage> GetProcessedAsync(string uploadKey, MediaPurpose purpose, string fieldName, CancellationToken cancellationToken);

    /// <summary>Removes the renditions and the manifest of an image. Failures are logged, not thrown.</summary>
    Task DeleteRenditionsAsync(string storageKey, int originalWidth, CancellationToken cancellationToken);
}

/// <summary>Content of <c>{storageKey}/manifest.json</c>.</summary>
public sealed record ImageManifest(string Status, int Width, int Height, IReadOnlyList<int> Widths)
{
    public const string Ready = "ready";
    public const string Rejected = "rejected";
}

public sealed class ImageProcessor(
    IMediaStorage storage,
    IImageTransformer transformer,
    IOptions<MediaOptions> options,
    TimeProvider timeProvider,
    ILogger<ImageProcessor> logger) : IImageProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public async Task<ImageProcessingResult> ProcessUploadAsync(string uploadKey, CancellationToken cancellationToken)
    {
        if (!MediaKeys.TryParseUploadKey(uploadKey, out var purpose, out var imageId))
        {
            return new ImageProcessingResult(ImageProcessingStatus.NotAnUploadKey);
        }

        var storageKey = MediaKeys.StorageKey(purpose, imageId);

        MediaObject? upload;
        try
        {
            upload = await storage.ReadAsync(uploadKey, MediaRules.MaxUploadBytes, cancellationToken);
        }
        catch (MediaObjectTooLargeException)
        {
            return await RejectAsync(uploadKey, storageKey, "too large", cancellationToken);
        }

        if (upload is null)
        {
            // Already handled by an earlier delivery of the same event, or never uploaded.
            var existing = await ReadManifestAsync(storageKey, cancellationToken);
            return existing switch
            {
                { Status: ImageManifest.Ready } => new ImageProcessingResult(ImageProcessingStatus.Processed, ToImage(storageKey, existing)),
                { Status: ImageManifest.Rejected } => new ImageProcessingResult(ImageProcessingStatus.Rejected),
                _ => new ImageProcessingResult(ImageProcessingStatus.UploadMissing),
            };
        }

        TransformedImage transformed;
        try
        {
            transformed = transformer.Transform(upload.Content);
        }
        catch (InvalidImageException exception)
        {
            return await RejectAsync(uploadKey, storageKey, exception.Message, cancellationToken);
        }

        foreach (var rendition in transformed.Renditions)
        {
            await storage.WriteAsync(
                MediaKeys.RenditionKey(storageKey, rendition.Width),
                rendition.Content,
                MediaRules.WebPContentType,
                MediaRules.ImmutableCacheControl,
                cancellationToken);
        }

        var manifest = new ImageManifest(
            ImageManifest.Ready, transformed.Width, transformed.Height, [.. transformed.Renditions.Select(r => r.Width)]);
        await WriteManifestAsync(storageKey, manifest, cancellationToken);
        await storage.DeleteAsync([uploadKey], cancellationToken);

        logger.LogInformation(
            "Image {StorageKey} processed: {Width}x{Height}, renditions {Widths}.",
            storageKey, transformed.Width, transformed.Height, manifest.Widths);

        return new ImageProcessingResult(ImageProcessingStatus.Processed, ToImage(storageKey, manifest));
    }

    public async Task<ProcessedImage> GetProcessedAsync(string uploadKey, MediaPurpose purpose, string fieldName, CancellationToken cancellationToken)
    {
        if (!MediaKeys.TryParseUploadKey(uploadKey, out var actualPurpose, out var imageId) || actualPurpose != purpose)
        {
            throw new RequestValidationException(fieldName, "Ključ otpremljene slike nije ispravan.");
        }

        var storageKey = MediaKeys.StorageKey(purpose, imageId);
        var settings = options.Value;

        var manifest = await ReadManifestAsync(storageKey, cancellationToken);
        if (manifest is null && settings.ProcessInline)
        {
            var result = await ProcessUploadAsync(uploadKey, cancellationToken);
            return result.Status switch
            {
                ImageProcessingStatus.Processed => result.Image!,
                ImageProcessingStatus.Rejected => throw InvalidImage(),
                _ => throw UploadNotFound(),
            };
        }

        if (manifest is null)
        {
            // The image processor Lambda is triggered by the upload; give it a moment.
            var deadline = timeProvider.GetUtcNow() + TimeSpan.FromSeconds(Math.Clamp(settings.ReadyWaitSeconds, 0, 25));
            while (manifest is null && timeProvider.GetUtcNow() < deadline)
            {
                await Task.Delay(PollInterval, timeProvider, cancellationToken);
                manifest = await ReadManifestAsync(storageKey, cancellationToken);
            }
        }

        return manifest switch
        {
            { Status: ImageManifest.Ready } => ToImage(storageKey, manifest),
            { Status: ImageManifest.Rejected } => throw InvalidImage(),
            _ => throw new ConflictException(ErrorTexts.ImageNotReadyDetail, ErrorCodes.ImageNotReady, ErrorTexts.ImageNotReadyTitle),
        };
    }

    public async Task DeleteRenditionsAsync(string storageKey, int originalWidth, CancellationToken cancellationToken)
    {
        try
        {
            await storage.DeleteAsync(MediaKeys.AllKeys(storageKey, originalWidth), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The database no longer refers to these files; leftovers cost storage only.
            logger.LogWarning(exception, "Renditions of {StorageKey} could not be deleted.", storageKey);
        }
    }

    private async Task<ImageProcessingResult> RejectAsync(string uploadKey, string storageKey, string reason, CancellationToken cancellationToken)
    {
        logger.LogWarning("Upload {UploadKey} rejected: {Reason}", uploadKey, reason);
        await WriteManifestAsync(storageKey, new ImageManifest(ImageManifest.Rejected, 0, 0, []), cancellationToken);
        await storage.DeleteAsync([uploadKey], cancellationToken);
        return new ImageProcessingResult(ImageProcessingStatus.Rejected);
    }

    private async Task<ImageManifest?> ReadManifestAsync(string storageKey, CancellationToken cancellationToken)
    {
        var stored = await storage.ReadAsync(MediaKeys.ManifestKey(storageKey), 64 * 1024, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize<ImageManifest>(stored.Content, JsonOptions);
        return manifest is { Status: ImageManifest.Ready, Width: > 0, Height: > 0, Widths.Count: > 0 } or { Status: ImageManifest.Rejected }
            ? manifest
            : throw new InvalidOperationException($"The manifest of '{storageKey}' is not readable.");
    }

    private Task WriteManifestAsync(string storageKey, ImageManifest manifest, CancellationToken cancellationToken) =>
        storage.WriteAsync(
            MediaKeys.ManifestKey(storageKey),
            JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
            "application/json",
            cacheControl: "no-store",
            cancellationToken);

    private static ProcessedImage ToImage(string storageKey, ImageManifest manifest) =>
        new(storageKey, manifest.Width, manifest.Height, manifest.Widths);

    private static BadRequestException InvalidImage() =>
        new(ErrorTexts.InvalidImageDetail, ErrorCodes.InvalidImage, ErrorTexts.InvalidImageTitle);

    private static BadRequestException UploadNotFound() =>
        new(ErrorTexts.UploadNotFoundDetail, ErrorCodes.UploadNotFound, ErrorTexts.UploadNotFoundTitle);
}
