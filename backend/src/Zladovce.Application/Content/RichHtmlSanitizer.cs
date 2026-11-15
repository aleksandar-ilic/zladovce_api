using AngleSharp.Dom;
using Ganss.Xss;
using Microsoft.Extensions.Options;
using Zladovce.Application.Media;

namespace Zladovce.Application.Content;

/// <summary>Cleans the rich text of blog posts and CMS pages before it is stored.</summary>
public interface IRichHtmlSanitizer
{
    /// <summary>The allowed HTML only; null or blank gives an empty string.</summary>
    string Sanitize(string? html);
}

/// <summary>
/// The allow-list of <see cref="Common.HtmlContentSanitizer"/> (paragraphs, emphasis, lists, h2–h4, quotes, links, hr) plus
/// <c>figure</c>, <c>figcaption</c> and <c>img[src, alt, width, height]</c>. An image is kept only when its <c>src</c> is an
/// image of this shop's media storage (<c>{Media:BaseUrl}/media/blog/…</c> or <c>…/media/pages/…</c>); any other host,
/// scheme or path removes the element. No scripts, styles, classes, event handlers, <c>javascript:</c> URLs, frames or forms.
/// </summary>
public sealed class RichHtmlSanitizer : IRichHtmlSanitizer
{
    private static readonly string[] AllowedTags =
        ["p", "br", "strong", "b", "em", "i", "u", "s", "ul", "ol", "li", "h2", "h3", "h4", "blockquote", "a", "hr", "figure", "figcaption", "img"];

    private static readonly HashSet<string> DroppedWithContent =
        new(StringComparer.OrdinalIgnoreCase) { "script", "style", "noscript", "template", "iframe", "object", "embed", "svg", "math", "textarea", "select" };

    private readonly HtmlSanitizer _sanitizer;
    private readonly string[] _allowedImagePrefixes;

    public RichHtmlSanitizer(IOptions<MediaOptions> options)
    {
        var baseUrl = options.Value.BaseUrl?.TrimEnd('/');
        _allowedImagePrefixes = string.IsNullOrWhiteSpace(baseUrl)
            ? []
            : [$"{baseUrl}/{MediaKeys.MediaPrefix}blog/", $"{baseUrl}/{MediaKeys.MediaPrefix}pages/"];

        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.Clear();
        foreach (var tag in AllowedTags)
        {
            _sanitizer.AllowedTags.Add(tag);
        }

        _sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "href", "title", "src", "alt", "width", "height" })
        {
            _sanitizer.AllowedAttributes.Add(attribute);
        }

        _sanitizer.UriAttributes.Clear();
        _sanitizer.UriAttributes.Add("href");
        _sanitizer.UriAttributes.Add("src");

        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.Add("http");
        _sanitizer.AllowedSchemes.Add("https");
        _sanitizer.AllowedSchemes.Add("mailto");
        _sanitizer.AllowedSchemes.Add("tel");

        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.AllowedAtRules.Clear();
        _sanitizer.AllowDataAttributes = false;
        _sanitizer.KeepChildNodes = true;

        _sanitizer.RemovingTag += (_, e) =>
        {
            if (DroppedWithContent.Contains(e.Tag.LocalName))
            {
                e.Tag.InnerHtml = string.Empty;
            }
        };

        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement element)
            {
                return;
            }

            if (element.LocalName == "img")
            {
                if (!IsAllowedImage(element.GetAttribute("src")))
                {
                    element.Remove();
                    return;
                }

                foreach (var size in new[] { "width", "height" })
                {
                    if (element.GetAttribute(size) is { } value && !(int.TryParse(value, out var px) && px is > 0 and <= 4000))
                    {
                        element.RemoveAttribute(size);
                    }
                }
            }
            else if (element.LocalName != "a" && element.HasAttribute("src"))
            {
                element.RemoveAttribute("src");
            }

            if (element.LocalName != "img")
            {
                element.RemoveAttribute("alt");
                element.RemoveAttribute("width");
                element.RemoveAttribute("height");
            }
        };
    }

    public string Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : _sanitizer.Sanitize(html).Trim();

    private bool IsAllowedImage(string? src)
    {
        if (string.IsNullOrWhiteSpace(src) || src.Contains("..", StringComparison.Ordinal) || src.Contains('\\'))
        {
            return false;
        }

        return Uri.TryCreate(src, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(uri.UserInfo)
            && _allowedImagePrefixes.Any(prefix => src.StartsWith(prefix, StringComparison.Ordinal));
    }
}
