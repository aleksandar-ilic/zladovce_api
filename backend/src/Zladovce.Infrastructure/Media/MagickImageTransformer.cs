using ImageMagick;
using Zladovce.Application.Media;

namespace Zladovce.Infrastructure.Media;

/// <summary>
/// <see cref="IImageTransformer"/> on Magick.NET (ImageMagick). The upload is untrusted, so:
/// the format is decided from the file signature and only JPEG, PNG and WebP are handed to the decoder
/// (with that format forced, so no other ImageMagick coder can be reached); pixel count is limited;
/// the output is always freshly encoded WebP without any metadata.
/// </summary>
public sealed class MagickImageTransformer : IImageTransformer
{
    private const int WebPQuality = 82;

    public TransformedImage Transform(byte[] source, int? emailPngMaxWidth = null)
    {
        var format = DetectFormat(source) ?? throw new InvalidImageException("The file is not a JPEG, PNG or WebP image.");

        try
        {
            var info = new MagickImageInfo(source, new MagickReadSettings { Format = format });
            if (info.Width == 0 || info.Height == 0 || (long)info.Width * info.Height > MediaRules.MaxPixels)
            {
                throw new InvalidImageException("The image dimensions are not acceptable.");
            }

            // First frame only: animated images become a still picture.
            using var image = new MagickImage(source, new MagickReadSettings { Format = format, FrameIndex = 0, FrameCount = 1 });

            // Apply the EXIF orientation to the pixels, then drop every profile and comment (EXIF, GPS, ICC, XMP).
            image.AutoOrient();
            if (image.GetColorProfile() is not null)
            {
                // Colours are converted to sRGB before the embedded profile is removed.
                image.TransformColorSpace(ColorProfiles.SRGB);
            }

            image.Strip();

            var width = (int)image.Width;
            var height = (int)image.Height;

            var renditions = new List<ImageRendition>();
            foreach (var targetWidth in RenditionPlan.WidthsFor(width))
            {
                using var rendition = image.Clone();
                if (targetWidth < width)
                {
                    rendition.Resize((uint)targetWidth, 0);
                }

                rendition.Strip();
                rendition.Format = MagickFormat.WebP;
                rendition.Quality = WebPQuality;
                renditions.Add(new ImageRendition(targetWidth, rendition.ToByteArray()));
            }

            byte[]? emailPng = null;
            if (emailPngMaxWidth is > 0 and var maxWidth)
            {
                using var png = image.Clone();
                if (width > maxWidth)
                {
                    png.Resize((uint)maxWidth, 0);
                }

                png.Strip();
                png.Format = MagickFormat.Png;
                emailPng = png.ToByteArray();
            }

            return new TransformedImage(width, height, renditions, emailPng);
        }
        catch (MagickException exception)
        {
            throw new InvalidImageException("The image could not be decoded.", exception);
        }
    }

    /// <summary>The image format according to the first bytes of the file, or null when it is none of the accepted ones.</summary>
    public static MagickFormat? DetectFormat(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return MagickFormat.Jpeg;
        }

        if (content.Length >= 8 && content[..8].SequenceEqual<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return MagickFormat.Png;
        }

        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8))
        {
            return MagickFormat.WebP;
        }

        return null;
    }
}
