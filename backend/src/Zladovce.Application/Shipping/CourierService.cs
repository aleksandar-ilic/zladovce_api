using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Domain.Shipping;

namespace Zladovce.Application.Shipping;

/// <summary>
/// Body of <c>POST /api/v1/admin/couriers</c> and <c>PUT /api/v1/admin/couriers/{id}</c>. Every member must be sent.
/// Couriers are deactivated (<c>isActive</c> false) instead of deleted.
/// </summary>
public sealed record SaveCourierRequest
{
    /// <summary>1–100 characters, unique (ignoring case).</summary>
    public required string? Name { get; init; }

    /// <summary>
    /// Null, or an absolute https URL of the courier's tracking page containing <c>{trackingNumber}</c>, e.g.
    /// <c>https://kurir.example/pracenje?broj={trackingNumber}</c>. Enter it only when it was verified. Up to 500 characters.
    /// </summary>
    public required string? TrackingUrlTemplate { get; init; }

    public required bool? IsActive { get; init; }

    /// <summary>0 or more; lists are sorted by it, then by name.</summary>
    public required int? SortOrder { get; init; }
}

public sealed class SaveCourierRequestValidator : AbstractValidator<SaveCourierRequest>
{
    public SaveCourierRequestValidator()
    {
        RuleFor(r => r.Name)
            .Cascade(CascadeMode.Stop)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Naziv je obavezan.")
            .Must(n => n!.Trim().Length <= CourierCompany.MaxNameLength).WithMessage($"Naziv može imati najviše {CourierCompany.MaxNameLength} znakova.");
        RuleFor(r => r.TrackingUrlTemplate)
            .Must(t => string.IsNullOrWhiteSpace(t) || CourierCompany.IsValidTemplate(t.Trim()))
            .WithMessage("Adresa za praćenje mora počinjati sa https:// i sadržati {trackingNumber} (najviše 500 znakova).");
        RuleFor(r => r.IsActive).NotNull();
        RuleFor(r => r.SortOrder).NotNull().GreaterThanOrEqualTo(0).WithMessage("Redosled mora biti 0 ili veći.");
    }
}

/// <param name="TrackingUrlTemplate">Null when no verified tracking page is configured.</param>
public sealed record CourierResponse(
    Guid Id,
    string Name,
    string? TrackingUrlTemplate,
    bool IsActive,
    int SortOrder,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CourierResponse From(CourierCompany c) => new(c.Id, c.Name, c.TrackingUrlTemplate, c.IsActive, c.SortOrder, c.CreatedAt, c.UpdatedAt);
}

public sealed record CourierListResponse(IReadOnlyList<CourierResponse> Items);

public interface ICourierService
{
    Task<CourierListResponse> ListAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<CourierResponse> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<CourierResponse> CreateAsync(SaveCourierRequest request, CancellationToken cancellationToken);
    Task<CourierResponse> UpdateAsync(Guid id, SaveCourierRequest request, CancellationToken cancellationToken);
}

/// <summary>Courier companies. Changes are audited by the audit interceptor (<c>CourierCompany.Created/Updated</c>).</summary>
public sealed class CourierService(IZladovceDbContext db, TimeProvider timeProvider) : ICourierService
{
    public async Task<CourierListResponse> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var couriers = await db.CourierCompanies.AsNoTracking()
            .Where(c => includeInactive || c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        return new CourierListResponse([.. couriers.Select(CourierResponse.From)]);
    }

    public async Task<CourierResponse> GetAsync(Guid id, CancellationToken cancellationToken) =>
        CourierResponse.From(await db.CourierCompanies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException());

    public async Task<CourierResponse> CreateAsync(SaveCourierRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name!.Trim();
        await EnsureNameFreeAsync(name, null, cancellationToken);

        var courier = CourierCompany.Create(name, request.TrackingUrlTemplate, request.IsActive!.Value, request.SortOrder!.Value, timeProvider.GetUtcNow());
        db.CourierCompanies.Add(courier);
        await db.SaveChangesAsync(cancellationToken);
        return CourierResponse.From(courier);
    }

    public async Task<CourierResponse> UpdateAsync(Guid id, SaveCourierRequest request, CancellationToken cancellationToken)
    {
        var courier = await db.CourierCompanies.SingleOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new NotFoundException();
        var name = request.Name!.Trim();
        await EnsureNameFreeAsync(name, id, cancellationToken);

        courier.Update(name, request.TrackingUrlTemplate, request.IsActive!.Value, request.SortOrder!.Value, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return CourierResponse.From(courier);
    }

    private async Task EnsureNameFreeAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        // citext column: the comparison ignores case.
        if (await db.CourierCompanies.AnyAsync(c => c.Name == name && c.Id != exceptId, cancellationToken))
        {
            throw NameTaken();
        }
    }

    public static ConflictException NameTaken() => new(
        ErrorTexts.CourierNameTakenDetail,
        ErrorCodes.CourierNameTaken,
        ErrorTexts.CourierNameTakenTitle,
        new Dictionary<string, string[]> { ["name"] = [ErrorTexts.CourierNameTakenDetail] });
}
