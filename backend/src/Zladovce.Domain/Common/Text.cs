namespace Zladovce.Domain.Common;

internal static class Text
{
    /// <summary>Trimmed value, or null when nothing is left.</summary>
    public static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
