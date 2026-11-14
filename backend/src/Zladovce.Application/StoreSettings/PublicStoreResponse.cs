using Zladovce.Domain.StoreSettings;

namespace Zladovce.Application.StoreSettings;

/// <summary>Response of <c>GET /api/v1/store</c>: the part of store settings that is safe to show publicly.</summary>
public sealed record PublicStoreResponse(
    string StoreName,
    string? LogoKey,
    PublicStoreContact Contact,
    PublicStoreSocial Social,
    PublicStoreShipping Shipping,
    PublicStoreSeo Seo)
{
    public static PublicStoreResponse From(StoreSettingsRecord settings) => new(
        settings.StoreName,
        settings.LogoKey,
        new PublicStoreContact(settings.ContactEmail, settings.Phone, settings.BusinessAddress),
        new PublicStoreSocial(
            settings.FacebookUrl,
            settings.InstagramUrl,
            settings.OtherLinks.Select(l => new PublicStoreLink(l.Label, l.Url)).ToList()),
        new PublicStoreShipping(
            settings.ShippingEnabled,
            settings.ShippingPrice,
            settings.FreeShippingThreshold,
            settings.MinOrderAmount,
            settings.EstimatedDeliveryText,
            settings.DeliveryInfo),
        new PublicStoreSeo(settings.DefaultSeoTitle, settings.DefaultSeoDescription));
}

public sealed record PublicStoreContact(string? Email, string? Phone, string? Address);

public sealed record PublicStoreSocial(string? FacebookUrl, string? InstagramUrl, IReadOnlyList<PublicStoreLink> OtherLinks);

public sealed record PublicStoreLink(string Label, string Url);

/// <param name="Enabled">False when the store currently does not ship.</param>
/// <param name="Price">Shipping price in RSD.</param>
/// <param name="FreeShippingThreshold">Order subtotal from which shipping is free; null when there is no free shipping.</param>
/// <param name="MinOrderAmount">Minimum order subtotal; null when there is no minimum.</param>
/// <param name="EstimatedDeliveryText">Short text such as an expected delivery time.</param>
/// <param name="DeliveryInfo">Longer delivery information shown to customers.</param>
public sealed record PublicStoreShipping(
    bool Enabled,
    decimal Price,
    decimal? FreeShippingThreshold,
    decimal? MinOrderAmount,
    string? EstimatedDeliveryText,
    string? DeliveryInfo);

public sealed record PublicStoreSeo(string? DefaultTitle, string? DefaultDescription);
