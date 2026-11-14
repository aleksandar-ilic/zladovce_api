namespace Zladovce.Domain.StoreSettings;

/// <summary>An additional public link of the store (stored inside <c>store_settings.other_links</c>).</summary>
public sealed record StoreLink(string Label, string Url);
