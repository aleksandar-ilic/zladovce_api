using System.Globalization;
using System.Text;

namespace Zladovce.Domain.Common;

/// <summary>URL slugs: lower-case ASCII letters and digits separated by single hyphens.</summary>
public static class Slug
{
    public const int MaxLength = 120;

    /// <summary>
    /// Builds a slug from free text. Serbian Latin letters are transliterated (č, ć → c; š → s; ž → z; đ → dj),
    /// other accents are removed, everything that is not a letter or digit becomes a hyphen.
    /// Returns an empty string when the text has no usable characters.
    /// </summary>
    public static string Generate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 8);
        var pendingHyphen = false;

        foreach (var c in text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC))
        {
            var mapped = c switch
            {
                'č' or 'ć' => "c",
                'š' => "s",
                'ž' => "z",
                'đ' => "dj",
                _ => null,
            };

            if (mapped is null)
            {
                mapped = RemoveAccent(c);
            }

            if (mapped.Length == 0)
            {
                pendingHyphen = builder.Length > 0;
                continue;
            }

            if (pendingHyphen)
            {
                builder.Append('-');
                pendingHyphen = false;
            }

            builder.Append(mapped);
        }

        if (builder.Length > MaxLength)
        {
            builder.Length = MaxLength;
        }

        return builder.ToString().Trim('-');
    }

    /// <summary>Whether the value is already a well-formed slug.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        var previousHyphen = false;
        foreach (var c in value)
        {
            if (c == '-')
            {
                if (previousHyphen)
                {
                    return false;
                }

                previousHyphen = true;
                continue;
            }

            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }

            previousHyphen = false;
        }

        return true;
    }

    /// <summary>The ASCII letter or digit behind a character, or an empty string when there is none.</summary>
    private static string RemoveAccent(char c)
    {
        if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
        {
            return c.ToString();
        }

        foreach (var part in c.ToString().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(part) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            return part is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? part.ToString() : string.Empty;
        }

        return string.Empty;
    }
}
