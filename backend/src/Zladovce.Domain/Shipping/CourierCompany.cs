using Zladovce.Domain.Common;

namespace Zladovce.Domain.Shipping;

/// <summary>
/// A courier company (table <c>courier_companies</c>), entered by the Owner. Never deleted: deactivated instead, so old
/// shipments keep their courier. No courier is seeded and no courier API is assumed.
/// </summary>
public sealed class CourierCompany
{
    public const string TrackingNumberPlaceholder = "{trackingNumber}";
    public const int MaxNameLength = 100;
    public const int MaxTemplateLength = 500;

    private CourierCompany()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Unique, case-insensitive.</summary>
    public string Name { get; private set; } = null!;

    /// <summary>
    /// Optional verified link to the courier's tracking page, an absolute https URL that contains
    /// <see cref="TrackingNumberPlaceholder"/>.
    /// </summary>
    public string? TrackingUrlTemplate { get; private set; }
    public bool IsActive { get; private set; }
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static CourierCompany Create(string name, string? trackingUrlTemplate, bool isActive, int sortOrder, DateTimeOffset now)
    {
        var courier = new CourierCompany { Id = Guid.CreateVersion7(now), CreatedAt = now };
        courier.Update(name, trackingUrlTemplate, isActive, sortOrder, now);
        return courier;
    }

    public void Update(string name, string? trackingUrlTemplate, bool isActive, int sortOrder, DateTimeOffset now)
    {
        var trimmedName = Text.NullIfBlank(name) ?? throw new DomainException("Courier name is required.");
        if (trimmedName.Length > MaxNameLength)
        {
            throw new DomainException("Courier name is too long.");
        }

        var template = Text.NullIfBlank(trackingUrlTemplate);
        if (template is not null && !IsValidTemplate(template))
        {
            throw new DomainException("The tracking URL template must be an https URL containing {trackingNumber}.");
        }

        if (sortOrder < 0)
        {
            throw new DomainException("Sort order cannot be negative.");
        }

        Name = trimmedName;
        TrackingUrlTemplate = template;
        IsActive = isActive;
        SortOrder = sortOrder;
        UpdatedAt = now;
    }

    /// <summary>An absolute https URL (with a host) that contains the placeholder, at most <see cref="MaxTemplateLength"/> characters.</summary>
    public static bool IsValidTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > MaxTemplateLength
            || !template.Contains(TrackingNumberPlaceholder, StringComparison.Ordinal)
            || template.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var sample = template.Replace(TrackingNumberPlaceholder, "TEST123", StringComparison.Ordinal);
        return Uri.TryCreate(sample, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    /// <summary>The courier's tracking page for a tracking number, or null without a template or number.</summary>
    public static string? TrackingUrl(string? template, string? trackingNumber) =>
        string.IsNullOrEmpty(template) || string.IsNullOrWhiteSpace(trackingNumber)
            ? null
            : template.Replace(TrackingNumberPlaceholder, Uri.EscapeDataString(trackingNumber.Trim()), StringComparison.Ordinal);
}

/// <summary>One shipping attempt of an order (table <c>shipments</c>). A failed delivery followed by a new attempt adds a row.</summary>
public sealed class Shipment
{
    public const int MaxTrackingNumberLength = 100;
    public const int MaxNoteLength = 1000;

    private Shipment()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid CourierCompanyId { get; private set; }

    /// <summary>The courier's own number; may be added later. Not the internal Zladovce tracking code.</summary>
    public string? TrackingNumber { get; private set; }
    public DateTimeOffset ShippedAt { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Shipment Create(Guid orderId, Guid courierCompanyId, string? trackingNumber, DateTimeOffset shippedAt, string? note, DateTimeOffset now)
    {
        if (orderId == Guid.Empty)
        {
            throw new DomainException("A shipment belongs to an order.");
        }

        var shipment = new Shipment { Id = Guid.CreateVersion7(now), OrderId = orderId, CreatedAt = now };
        shipment.Update(courierCompanyId, trackingNumber, shippedAt, note, now);
        return shipment;
    }

    public void Update(Guid courierCompanyId, string? trackingNumber, DateTimeOffset shippedAt, string? note, DateTimeOffset now)
    {
        if (courierCompanyId == Guid.Empty)
        {
            throw new DomainException("A shipment needs a courier company.");
        }

        var number = Text.NullIfBlank(trackingNumber);
        if (number is { Length: > MaxTrackingNumberLength })
        {
            throw new DomainException("Tracking number is too long.");
        }

        var trimmedNote = Text.NullIfBlank(note);
        if (trimmedNote is { Length: > MaxNoteLength })
        {
            throw new DomainException("Shipment note is too long.");
        }

        CourierCompanyId = courierCompanyId;
        TrackingNumber = number;
        ShippedAt = shippedAt.ToUniversalTime();
        Note = trimmedNote;
        UpdatedAt = now;
    }
}
