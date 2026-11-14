using System.Diagnostics.CodeAnalysis;

namespace Zladovce.Application.Media;

/// <summary>What an uploaded image is for. Decides the <c>media/…</c> folder of its renditions.</summary>
public enum MediaPurpose
{
    Product,
    Category,
}

/// <summary>Configuration section <c>Media</c>.</summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>Bucket holding <c>uploads/</c> (raw, private) and <c>media/</c> (processed, public through the CDN).</summary>
    public string? BucketName { get; set; }

    /// <summary>Public base URL without a trailing slash. An object is served at <c>{BaseUrl}/{key}</c>, for example <c>{BaseUrl}/media/products/{id}/800.webp</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>AWS region of the bucket. Defaults to the region of the running environment.</summary>
    public string? Region { get; set; }

    /// <summary>Endpoint of an S3-compatible store (MinIO in local development). Empty = Amazon S3.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Path-style addressing (<c>{ServiceUrl}/{bucket}/{key}</c>); needed for MinIO.</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>Static access key for a local S3-compatible store only. On AWS the execution role is used.</summary>
    public string? AccessKey { get; set; }

    /// <summary>Static secret key for a local S3-compatible store only.</summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// When true, attaching an image whose renditions do not exist yet processes the upload inside the API request.
    /// When false, the image processor Lambda (S3 trigger) does it and the API only waits for the result.
    /// </summary>
    public bool ProcessInline { get; set; }

    /// <summary>How long the API waits for the image processor before answering <c>IMAGE_NOT_READY</c> (only when <see cref="ProcessInline"/> is false).</summary>
    public int ReadyWaitSeconds { get; set; } = 8;

    /// <summary>Lifetime of a pre-signed upload.</summary>
    public int UploadUrlLifetimeMinutes { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BucketName) && !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Public URL of a storage key.</summary>
    public string UrlFor(string key) => $"{BaseUrl?.TrimEnd('/')}/{key}";
}

/// <summary>Fixed rules of image uploads.</summary>
public static class MediaRules
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;

    /// <summary>Upper bound of decoded pixels; protects the processor from decompression bombs.</summary>
    public const long MaxPixels = 60_000_000;

    public const string WebPContentType = "image/webp";

    /// <summary>Processed files never change (a new image gets a new key), so they may be cached for a year.</summary>
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    /// <summary>Accepted upload content types and the extension used in the upload key.</summary>
    public static readonly IReadOnlyDictionary<string, string> AllowedContentTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
    };
}

/// <summary>Which rendition widths are produced for an original of a given width.</summary>
public static class RenditionPlan
{
    public static readonly IReadOnlyList<int> StandardWidths = [400, 800, 1600];

    /// <summary>
    /// Every standard width that does not exceed the original, plus the original width itself when it is
    /// smaller than the largest standard width. Images are never enlarged.
    /// Examples: 3000 → 400, 800, 1600; 1000 → 400, 800, 1000; 300 → 300.
    /// </summary>
    public static IReadOnlyList<int> WidthsFor(int originalWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(originalWidth, 1);

        var widths = StandardWidths.Where(w => w <= originalWidth).ToList();
        if (originalWidth < StandardWidths[^1] && !widths.Contains(originalWidth))
        {
            widths.Add(originalWidth);
        }

        return widths;
    }
}

/// <summary>Layout of object keys in the media bucket.</summary>
public static class MediaKeys
{
    public const string UploadsPrefix = "uploads/";
    public const string MediaPrefix = "media/";

    public static string PurposeName(MediaPurpose purpose) => purpose switch
    {
        MediaPurpose.Product => "product",
        MediaPurpose.Category => "category",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    public static bool TryParsePurpose(string? name, out MediaPurpose purpose)
    {
        switch (name)
        {
            case "product":
                purpose = MediaPurpose.Product;
                return true;
            case "category":
                purpose = MediaPurpose.Category;
                return true;
            default:
                purpose = default;
                return false;
        }
    }

    /// <summary><c>uploads/{purpose}/{imageId}.{ext}</c> – where the browser puts the raw file.</summary>
    public static string UploadKey(MediaPurpose purpose, Guid imageId, string contentType) =>
        $"{UploadsPrefix}{PurposeName(purpose)}/{imageId:D}.{MediaRules.AllowedContentTypes[contentType]}";

    /// <summary>Strict parser of upload keys; anything else is refused.</summary>
    public static bool TryParseUploadKey(string? key, out MediaPurpose purpose, out Guid imageId)
    {
        purpose = default;
        imageId = default;

        if (key is null || !key.StartsWith(UploadsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = key[UploadsPrefix.Length..].Split('/');
        if (parts.Length != 2 || !TryParsePurpose(parts[0], out purpose))
        {
            return false;
        }

        var dot = parts[1].LastIndexOf('.');
        if (dot <= 0
            || !MediaRules.AllowedContentTypes.Values.Contains(parts[1][(dot + 1)..])
            || !Guid.TryParseExact(parts[1][..dot], "D", out imageId)
            || imageId == Guid.Empty)
        {
            return false;
        }

        // Round trip: rejects upper-case and other alternative spellings of the same key.
        return key == $"{UploadsPrefix}{parts[0]}/{imageId:D}{parts[1][dot..]}";
    }

    /// <summary>Base key of the renditions of an image: <c>media/products/{imageId}</c> or <c>media/categories/{imageId}</c>.</summary>
    public static string StorageKey(MediaPurpose purpose, Guid imageId) => purpose switch
    {
        MediaPurpose.Product => $"{MediaPrefix}products/{imageId:D}",
        MediaPurpose.Category => $"{MediaPrefix}categories/{imageId:D}",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    public static string RenditionKey(string storageKey, int width) => $"{storageKey}/{width}.webp";

    /// <summary>Written last by the processor; its presence means the renditions are complete.</summary>
    public static string ManifestKey(string storageKey) => $"{storageKey}/manifest.json";

    /// <summary>Every object that belongs to a processed image.</summary>
    public static IReadOnlyList<string> AllKeys(string storageKey, int originalWidth) =>
        [.. RenditionPlan.WidthsFor(originalWidth).Select(w => RenditionKey(storageKey, w)), ManifestKey(storageKey)];

    public static bool IsStorageKey(string? key, MediaPurpose purpose, [NotNullWhen(true)] out Guid? imageId)
    {
        imageId = null;
        var prefix = StorageKey(purpose, Guid.Empty);
        prefix = prefix[..(prefix.LastIndexOf('/') + 1)];
        if (key is null || !key.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(key[prefix.Length..], "D", out var id))
        {
            return false;
        }

        imageId = id;
        return true;
    }
}
