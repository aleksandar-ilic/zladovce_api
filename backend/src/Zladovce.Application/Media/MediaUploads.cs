using FluentValidation;
using Microsoft.Extensions.Options;

namespace Zladovce.Application.Media;

/// <summary>Body of <c>POST /api/v1/admin/media/upload-url</c>.</summary>
/// <param name="FileName">Original file name; informative only, the storage key is generated.</param>
/// <param name="ContentType"><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>. The upload must send exactly this type.</param>
/// <param name="Size">File size in bytes, 1 to 10485760 (10 MB).</param>
/// <param name="Purpose"><c>product</c> or <c>category</c>.</param>
public sealed record CreateUploadUrlRequest(string FileName, string ContentType, long Size, string Purpose);

/// <summary>A pre-signed upload. POST a <c>multipart/form-data</c> form to <see cref="UploadUrl"/> with every entry of <see cref="Fields"/> followed by the file in a field named <c>file</c>.</summary>
/// <param name="UploadUrl">Address of the storage (not of this API).</param>
/// <param name="Fields">Form fields to send unchanged, before the file.</param>
/// <param name="Key">Upload key to pass to the attach endpoint (product image) or as <c>imageKey</c> (category).</param>
/// <param name="ExpiresAt">The upload must start before this moment.</param>
/// <param name="MaxSize">Largest accepted file in bytes.</param>
public sealed record UploadUrlResponse(
    string UploadUrl,
    IReadOnlyDictionary<string, string> Fields,
    string Key,
    DateTimeOffset ExpiresAt,
    long MaxSize);

public sealed class CreateUploadUrlRequestValidator : AbstractValidator<CreateUploadUrlRequest>
{
    public CreateUploadUrlRequestValidator()
    {
        RuleFor(r => r.FileName).NotEmpty().MaximumLength(255);
        RuleFor(r => r.ContentType)
            .NotEmpty()
            .Must(type => type is null || MediaRules.AllowedContentTypes.ContainsKey(type))
            .WithMessage("Dozvoljeni formati su JPEG, PNG i WebP.");
        RuleFor(r => r.Size)
            .GreaterThan(0)
            .LessThanOrEqualTo(MediaRules.MaxUploadBytes)
            .WithMessage("Slika može imati najviše 10 MB.");
        RuleFor(r => r.Purpose)
            .NotEmpty()
            .Must(purpose => purpose is null || MediaKeys.TryParsePurpose(purpose, out _))
            .WithMessage("Namena mora biti 'product' ili 'category'.");
    }
}

public interface IMediaUploadService
{
    Task<UploadUrlResponse> CreateUploadUrlAsync(CreateUploadUrlRequest request, CancellationToken cancellationToken);
}

public sealed class MediaUploadService(IMediaStorage storage, IOptions<MediaOptions> options, TimeProvider timeProvider) : IMediaUploadService
{
    public async Task<UploadUrlResponse> CreateUploadUrlAsync(CreateUploadUrlRequest request, CancellationToken cancellationToken)
    {
        MediaKeys.TryParsePurpose(request.Purpose, out var purpose);
        var key = MediaKeys.UploadKey(purpose, Guid.CreateVersion7(timeProvider.GetUtcNow()), request.ContentType!);

        var upload = await storage.CreatePresignedUploadAsync(
            key,
            request.ContentType!,
            MediaRules.MaxUploadBytes,
            TimeSpan.FromMinutes(Math.Clamp(options.Value.UploadUrlLifetimeMinutes, 1, 60)),
            cancellationToken);

        return new UploadUrlResponse(upload.Url, upload.Fields, key, upload.ExpiresAt, MediaRules.MaxUploadBytes);
    }
}

/// <summary>An image as the administration sees it.</summary>
/// <param name="BaseUrl">Folder of the renditions. A rendition is at <c>{baseUrl}/{width}.webp</c>.</param>
/// <param name="Widths">Available rendition widths in pixels, ascending.</param>
/// <param name="Width">Width of the original in pixels.</param>
/// <param name="Height">Height of the original in pixels.</param>
public sealed record ImageRenditions(string BaseUrl, IReadOnlyList<int> Widths, int Width, int Height)
{
    public static ImageRenditions From(MediaOptions options, string storageKey, int width, int height) =>
        new(options.UrlFor(storageKey), RenditionPlan.WidthsFor(width), width, height);
}
