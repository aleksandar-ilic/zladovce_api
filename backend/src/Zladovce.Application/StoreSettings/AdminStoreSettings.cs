using FluentValidation;
using Zladovce.Application.Orders;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.Application.StoreSettings;

/// <summary>Response of <c>GET/PUT /api/v1/admin/settings</c>: every column of the store settings.</summary>
/// <param name="LogoKey">Storage key of the logo (<c>media/logos/{id}</c>) or null; send it back as <c>logoKey</c> to keep the logo.</param>
/// <param name="Logo">The logo renditions (null without a logo).</param>
/// <param name="UpdatedByAdminName">Who saved last; null before the first save.</param>
/// <param name="Version">Send back with the update; a stale value gives 409 <c>CONCURRENT_EDIT</c>.</param>
public sealed record AdminStoreSettingsResponse(
    string StoreName,
    string? LogoKey,
    StoreLogo? Logo,
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
    IReadOnlyList<StoreLinkDto> OtherLinks,
    string? DefaultSeoTitle,
    string? DefaultSeoDescription,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedByAdminId,
    string? UpdatedByAdminName,
    uint Version);

/// <summary>An additional public link (label and https URL).</summary>
public sealed record StoreLinkDto
{
    /// <summary>1–50 characters.</summary>
    public required string? Label { get; init; }

    /// <summary>Absolute https URL, up to 500 characters.</summary>
    public required string? Url { get; init; }
}

/// <summary>Body of <c>PUT /api/v1/admin/settings</c> (Owner only). Replaces every value, so every member must be sent.</summary>
public sealed record UpdateStoreSettingsRequest
{
    public required uint? Version { get; init; }

    /// <summary>1–100 characters.</summary>
    public required string? StoreName { get; init; }

    /// <summary>
    /// The current <c>logoKey</c> keeps the logo; null removes it; an upload key from <c>POST /admin/media/upload-url</c> (purpose
    /// <c>logo</c>) replaces it. Logos get WebP renditions and a PNG of at most 320 px for emails.
    /// </summary>
    public required string? LogoKey { get; init; }

    public required string? ContactEmail { get; init; }

    /// <summary>Shown as entered (up to 50 characters).</summary>
    public required string? Phone { get; init; }

    /// <summary>Up to 300 characters.</summary>
    public required string? BusinessAddress { get; init; }

    /// <summary>False stops all orders (checkout answers 409 <c>SHIPPING_DISABLED</c>).</summary>
    public required bool? ShippingEnabled { get; init; }

    /// <summary>0 or more, two decimals.</summary>
    public required decimal? ShippingPrice { get; init; }

    /// <summary>Null = no free shipping; otherwise more than 0.</summary>
    public required decimal? FreeShippingThreshold { get; init; }

    /// <summary>Null = no minimum; otherwise more than 0. Compared with the subtotal (without shipping).</summary>
    public required decimal? MinOrderAmount { get; init; }

    /// <summary>Short text such as "2–4 radna dana", up to 200 characters.</summary>
    public required string? EstimatedDeliveryText { get; init; }

    /// <summary>Longer delivery information, up to 4000 characters (plain text).</summary>
    public required string? DeliveryInfo { get; init; }

    /// <summary>Absolute https URL or null.</summary>
    public required string? FacebookUrl { get; init; }

    /// <summary>Absolute https URL or null.</summary>
    public required string? InstagramUrl { get; init; }

    /// <summary>Up to 10 links.</summary>
    public required IReadOnlyList<StoreLinkDto>? OtherLinks { get; init; }

    /// <summary>Up to 120 characters.</summary>
    public required string? DefaultSeoTitle { get; init; }

    /// <summary>Up to 320 characters.</summary>
    public required string? DefaultSeoDescription { get; init; }

    public StoreSettingsValues ToValues() => new(
        StoreName!,
        ContactEmail,
        Phone,
        BusinessAddress,
        ShippingEnabled!.Value,
        ShippingPrice!.Value,
        FreeShippingThreshold,
        MinOrderAmount,
        EstimatedDeliveryText,
        DeliveryInfo,
        FacebookUrl,
        InstagramUrl,
        [.. OtherLinks!.Select(l => new StoreLink(l.Label!, l.Url!))],
        DefaultSeoTitle,
        DefaultSeoDescription);
}

public static class StoreSettingsRules
{
    public const int MaxOtherLinks = 10;

    public static bool IsHttpsUrl(string? value) =>
        value is { Length: <= 500 }
        && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host);
}

public sealed class UpdateStoreSettingsRequestValidator : AbstractValidator<UpdateStoreSettingsRequest>
{
    private const string HttpsMessage = "Unesite punu adresu koja počinje sa https:// (najviše 500 znakova).";

    public UpdateStoreSettingsRequestValidator()
    {
        RuleFor(r => r.Version).NotNull().WithMessage("Verzija podešavanja je obavezna.");
        RuleFor(r => r.StoreName)
            .Cascade(CascadeMode.Stop)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Naziv prodavnice je obavezan.")
            .Must(v => v!.Trim().Length <= 100).WithMessage("Naziv prodavnice može imati najviše 100 znakova.");
        RuleFor(r => r.ContactEmail)
            .Must(v => string.IsNullOrWhiteSpace(v) || (v.Trim().Length <= CheckoutRules.MaxEmailLength && CheckoutRules.EmailPattern().IsMatch(v.Trim())))
            .WithMessage("Unesite ispravnu adresu e-pošte.");
        RuleFor(r => r.LogoKey).MaximumLength(200);
        RuleFor(r => r.Phone).MaximumLength(50).WithMessage("Telefon može imati najviše 50 znakova.");
        RuleFor(r => r.BusinessAddress).MaximumLength(300).WithMessage("Adresa može imati najviše 300 znakova.");
        RuleFor(r => r.ShippingEnabled).NotNull();
        RuleFor(r => r.ShippingPrice)
            .NotNull().WithMessage("Cena dostave je obavezna (0 za besplatnu).")
            .GreaterThanOrEqualTo(0).WithMessage("Cena dostave ne može biti negativna.");
        Money(RuleFor(r => r.ShippingPrice));
        RuleFor(r => r.FreeShippingThreshold).GreaterThan(0).WithMessage("Prag za besplatnu dostavu mora biti veći od nule ili prazan.");
        Money(RuleFor(r => r.FreeShippingThreshold));
        RuleFor(r => r.MinOrderAmount).GreaterThan(0).WithMessage("Minimalni iznos porudžbine mora biti veći od nule ili prazan.");
        Money(RuleFor(r => r.MinOrderAmount));
        RuleFor(r => r.EstimatedDeliveryText).MaximumLength(200).WithMessage("Rok isporuke može imati najviše 200 znakova.");
        RuleFor(r => r.DeliveryInfo).MaximumLength(4000).WithMessage("Informacije o dostavi mogu imati najviše 4000 znakova.");
        RuleFor(r => r.FacebookUrl).Must(v => string.IsNullOrWhiteSpace(v) || StoreSettingsRules.IsHttpsUrl(v)).WithMessage(HttpsMessage);
        RuleFor(r => r.InstagramUrl).Must(v => string.IsNullOrWhiteSpace(v) || StoreSettingsRules.IsHttpsUrl(v)).WithMessage(HttpsMessage);
        RuleFor(r => r.OtherLinks)
            .NotNull().WithMessage("Lista linkova je obavezna (može biti prazna).")
            .Must(l => l is null || l.Count <= StoreSettingsRules.MaxOtherLinks).WithMessage($"Najviše {StoreSettingsRules.MaxOtherLinks} dodatnih linkova.");
        RuleForEach(r => r.OtherLinks).NotNull().ChildRules(link =>
        {
            link.RuleFor(l => l.Label)
                .Cascade(CascadeMode.Stop)
                .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Naziv linka je obavezan.")
                .Must(v => v!.Trim().Length <= 50).WithMessage("Naziv linka može imati najviše 50 znakova.");
            link.RuleFor(l => l.Url).Must(StoreSettingsRules.IsHttpsUrl).WithMessage(HttpsMessage);
        });
        RuleFor(r => r.DefaultSeoTitle).MaximumLength(120).WithMessage("SEO naslov može imati najviše 120 znakova.");
        RuleFor(r => r.DefaultSeoDescription).MaximumLength(320).WithMessage("SEO opis može imati najviše 320 znakova.");
    }

    private static void Money(IRuleBuilderInitial<UpdateStoreSettingsRequest, decimal?> rule) =>
        rule.Must(v => v is null || (Pricing.HasAtMostTwoDecimals(v.Value) && v.Value <= Pricing.MaxPrice))
            .WithMessage("Iznos može imati najviše dve decimale.");
}
