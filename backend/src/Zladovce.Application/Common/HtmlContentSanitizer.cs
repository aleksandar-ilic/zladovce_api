using Ganss.Xss;

namespace Zladovce.Application.Common;

/// <summary>Cleans administrator-entered HTML before it is stored, so that stored content is always safe to render.</summary>
public interface IHtmlContentSanitizer
{
    /// <summary>Returns the HTML with everything outside the allow-list removed. Null or blank gives an empty string.</summary>
    string Sanitize(string? html);
}

/// <summary>
/// Allow-list: basic text formatting, lists, headings below the page title, quotes and links.
/// No scripts, styles, classes, event handlers, images, frames or forms.
/// </summary>
public sealed class HtmlContentSanitizer : IHtmlContentSanitizer
{
    private static readonly string[] AllowedTags =
        ["p", "br", "strong", "b", "em", "i", "u", "s", "ul", "ol", "li", "h2", "h3", "h4", "blockquote", "a", "hr"];

    private readonly HtmlSanitizer _sanitizer;

    public HtmlContentSanitizer()
    {
        _sanitizer = new HtmlSanitizer();

        _sanitizer.AllowedTags.Clear();
        foreach (var tag in AllowedTags)
        {
            _sanitizer.AllowedTags.Add(tag);
        }

        _sanitizer.AllowedAttributes.Clear();
        _sanitizer.AllowedAttributes.Add("href");
        _sanitizer.AllowedAttributes.Add("title");

        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.Add("http");
        _sanitizer.AllowedSchemes.Add("https");
        _sanitizer.AllowedSchemes.Add("mailto");
        _sanitizer.AllowedSchemes.Add("tel");

        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.AllowedAtRules.Clear();
        _sanitizer.AllowDataAttributes = false;
        _sanitizer.KeepChildNodes = true;

        // Text of unknown wrappers (span, div from pasted content) is kept, but the content of these elements is code or
        // embedded material and is dropped together with the element.
        _sanitizer.RemovingTag += (_, e) =>
        {
            if (DroppedWithContent.Contains(e.Tag.LocalName))
            {
                e.Tag.InnerHtml = string.Empty;
            }
        };
    }

    private static readonly HashSet<string> DroppedWithContent =
        new(StringComparer.OrdinalIgnoreCase) { "script", "style", "noscript", "template", "iframe", "object", "embed", "svg", "math", "textarea", "select" };

    public string Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : _sanitizer.Sanitize(html).Trim();
}
