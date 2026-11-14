namespace Zladovce.Domain.StoreSettings;

/// <summary>An additional public link of the store (stored inside <c>store_settings.other_links</c>).</summary>
public sealed record StoreLink(string Label, string Url);

/// <summary>Every value of the store settings the Owner edits in the admin application (all but the logo).</summary>
public sealed record StoreSettingsValues(
    string StoreName,
    string? ContactEmail,
    string? Phone,
    string? BusinessAddress,
    bool ShippingEnabled,
    decimal ShippingPrice,
    decimal? FreeShippingThreshold,
    decimal? MinOrderAmount,
    string? EstimatedDeliveryText,
    string? DeliveryInfo,
    string? FacebookUrl,
    string? InstagramUrl,
    IReadOnlyList<StoreLink> OtherLinks,
    string? DefaultSeoTitle,
    string? DefaultSeoDescription);
