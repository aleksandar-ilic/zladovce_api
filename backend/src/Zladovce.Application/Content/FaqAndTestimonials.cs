using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Common;
using Zladovce.Domain.Content;

namespace Zladovce.Application.Content;

/// <summary>Body of <c>POST /api/v1/admin/faq</c> (added at the end) and <c>PUT /api/v1/admin/faq/{id}</c>. Every member must be sent.</summary>
public sealed record SaveFaqItemRequest
{
    /// <summary>1–300 characters.</summary>
    public required string? Question { get; init; }

    /// <summary>Plain text, 1–4000 characters (line breaks kept).</summary>
    public required string? Answer { get; init; }

    /// <summary>Only published items are public.</summary>
    public required bool? IsPublished { get; init; }
}

public sealed class SaveFaqItemRequestValidator : AbstractValidator<SaveFaqItemRequest>
{
    public SaveFaqItemRequestValidator()
    {
        RuleFor(r => r.Question)
            .Cascade(CascadeMode.Stop)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Pitanje je obavezno.")
            .Must(v => v!.Trim().Length <= FaqItem.MaxQuestionLength).WithMessage($"Pitanje može imati najviše {FaqItem.MaxQuestionLength} znakova.");
        RuleFor(r => r.Answer)
            .Cascade(CascadeMode.Stop)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Odgovor je obavezan.")
            .Must(v => v!.Trim().Length <= FaqItem.MaxAnswerLength).WithMessage($"Odgovor može imati najviše {FaqItem.MaxAnswerLength} znakova.");
        RuleFor(r => r.IsPublished).NotNull();
    }
}

/// <summary>Body of <c>PUT /api/v1/admin/faq/order</c>: every FAQ item exactly once, in the new order.</summary>
public sealed record ReorderFaqRequest
{
    public required IReadOnlyList<Guid>? Ids { get; init; }
}

public sealed class ReorderFaqRequestValidator : AbstractValidator<ReorderFaqRequest>
{
    public ReorderFaqRequestValidator() =>
        RuleFor(r => r.Ids).NotNull().Must(ids => ids is null || ids.Distinct().Count() == ids.Count).WithMessage("Svaka stavka sme da se navede samo jednom.");
}

public sealed record AdminFaqItemResponse(Guid Id, string Question, string Answer, int SortOrder, bool IsPublished, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static AdminFaqItemResponse From(FaqItem f) => new(f.Id, f.Question, f.Answer, f.SortOrder, f.IsPublished, f.CreatedAt, f.UpdatedAt);
}

public sealed record AdminFaqListResponse(IReadOnlyList<AdminFaqItemResponse> Items);

public sealed record PublicFaqItem(Guid Id, string Question, string Answer);

public sealed record PublicFaqResponse(IReadOnlyList<PublicFaqItem> Items);

/// <summary>Body of <c>POST /api/v1/admin/testimonials</c> and <c>PUT …/{id}</c>. Enter only real testimonials (A13). Every member must be sent.</summary>
public sealed record SaveTestimonialRequest
{
    /// <summary>1–100 characters, as the customer agreed to be named.</summary>
    public required string? AuthorName { get; init; }

    /// <summary>Optional place, up to 100 characters.</summary>
    public required string? Place { get; init; }

    /// <summary>1–1000 characters, plain text.</summary>
    public required string? Text { get; init; }

    public required bool? IsPublished { get; init; }

    /// <summary>0 or more; lists are sorted by it, then newest first.</summary>
    public required int? SortOrder { get; init; }
}

public sealed class SaveTestimonialRequestValidator : AbstractValidator<SaveTestimonialRequest>
{
    public SaveTestimonialRequestValidator()
    {
        RuleFor(r => r.AuthorName)
            .Cascade(CascadeMode.Stop)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Ime je obavezno.")
            .Must(v => v!.Trim().Length <= Testimonial.MaxAuthorLength).WithMessage($"Ime može imati najviše {Testimonial.MaxAuthorLength} znakova.");
        RuleFor(r => r.Place).MaximumLength(Testimonial.MaxPlaceLength).WithMessage($"Mesto može imati najviše {Testimonial.MaxPlaceLength} znakova.");
        RuleFor(r => r.Text)
            .Cascade(CascadeMode.Stop)
            .Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Tekst je obavezan.")
            .Must(v => v!.Trim().Length <= Testimonial.MaxTextLength).WithMessage($"Tekst može imati najviše {Testimonial.MaxTextLength} znakova.");
        RuleFor(r => r.IsPublished).NotNull();
        RuleFor(r => r.SortOrder).NotNull().InclusiveBetween(0, 100_000).WithMessage("Redosled mora biti između 0 i 100000.");
    }
}

public sealed record AdminTestimonialResponse(Guid Id, string AuthorName, string? Place, string Text, bool IsPublished, int SortOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static AdminTestimonialResponse From(Testimonial t) => new(t.Id, t.AuthorName, t.Place, t.Text, t.IsPublished, t.SortOrder, t.CreatedAt, t.UpdatedAt);
}

public sealed record AdminTestimonialListResponse(IReadOnlyList<AdminTestimonialResponse> Items);

public sealed record PublicTestimonial(Guid Id, string AuthorName, string? Place, string Text);

/// <summary>Empty unless the owner published real testimonials: the homepage hides the section then.</summary>
public sealed record PublicTestimonialsResponse(IReadOnlyList<PublicTestimonial> Items);

public interface IFaqService
{
    Task<AdminFaqListResponse> ListAsync(CancellationToken cancellationToken);
    Task<AdminFaqItemResponse> CreateAsync(SaveFaqItemRequest request, CancellationToken cancellationToken);
    Task<AdminFaqItemResponse> UpdateAsync(Guid id, SaveFaqItemRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<AdminFaqListResponse> ReorderAsync(ReorderFaqRequest request, CancellationToken cancellationToken);
    Task<PublicFaqResponse> PublicAsync(CancellationToken cancellationToken);
}

public interface ITestimonialService
{
    Task<AdminTestimonialListResponse> ListAsync(CancellationToken cancellationToken);
    Task<AdminTestimonialResponse> CreateAsync(SaveTestimonialRequest request, CancellationToken cancellationToken);
    Task<AdminTestimonialResponse> UpdateAsync(Guid id, SaveTestimonialRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
    Task<PublicTestimonialsResponse> PublicAsync(CancellationToken cancellationToken);
}

/// <summary>FAQ (audited by the interceptor: <c>FaqItem.*</c>).</summary>
public sealed class FaqService(IZladovceDbContext db, TimeProvider timeProvider) : IFaqService
{
    public async Task<AdminFaqListResponse> ListAsync(CancellationToken cancellationToken) =>
        new([.. (await Ordered(db.FaqItems.AsNoTracking()).ToListAsync(cancellationToken)).Select(AdminFaqItemResponse.From)]);

    public async Task<AdminFaqItemResponse> CreateAsync(SaveFaqItemRequest request, CancellationToken cancellationToken)
    {
        var next = (await db.FaqItems.MaxAsync(f => (int?)f.SortOrder, cancellationToken) ?? -1) + 1;
        var item = FaqItem.Create(request.Question!, request.Answer!, request.IsPublished!.Value, next, timeProvider.GetUtcNow());
        db.FaqItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return AdminFaqItemResponse.From(item);
    }

    public async Task<AdminFaqItemResponse> UpdateAsync(Guid id, SaveFaqItemRequest request, CancellationToken cancellationToken)
    {
        var item = await db.FaqItems.SingleOrDefaultAsync(f => f.Id == id, cancellationToken) ?? throw new NotFoundException();
        item.Update(request.Question!, request.Answer!, request.IsPublished!.Value, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return AdminFaqItemResponse.From(item);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.FaqItems.SingleOrDefaultAsync(f => f.Id == id, cancellationToken) ?? throw new NotFoundException();
        db.FaqItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AdminFaqListResponse> ReorderAsync(ReorderFaqRequest request, CancellationToken cancellationToken)
    {
        var items = await db.FaqItems.ToListAsync(cancellationToken);
        var ids = request.Ids!;
        if (ids.Count != items.Count || ids.Any(id => items.All(i => i.Id != id)))
        {
            throw new RequestValidationException("ids", "Redosled mora da sadrži sva pitanja, svako tačno jednom.");
        }

        var now = timeProvider.GetUtcNow();
        for (var position = 0; position < ids.Count; position++)
        {
            items.First(i => i.Id == ids[position]).SetSortOrder(position, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ListAsync(cancellationToken);
    }

    public async Task<PublicFaqResponse> PublicAsync(CancellationToken cancellationToken) =>
        new(await Ordered(db.FaqItems.AsNoTracking().Where(f => f.IsPublished)).Select(f => new PublicFaqItem(f.Id, f.Question, f.Answer)).ToListAsync(cancellationToken));

    private static IQueryable<FaqItem> Ordered(IQueryable<FaqItem> query) => query.OrderBy(f => f.SortOrder).ThenBy(f => f.CreatedAt).ThenBy(f => f.Id);
}

/// <summary>Testimonials (audited by the interceptor: <c>Testimonial.*</c>).</summary>
public sealed class TestimonialService(IZladovceDbContext db, TimeProvider timeProvider) : ITestimonialService
{
    public async Task<AdminTestimonialListResponse> ListAsync(CancellationToken cancellationToken) =>
        new([.. (await Ordered(db.Testimonials.AsNoTracking()).ToListAsync(cancellationToken)).Select(AdminTestimonialResponse.From)]);

    public async Task<AdminTestimonialResponse> CreateAsync(SaveTestimonialRequest request, CancellationToken cancellationToken)
    {
        var testimonial = Testimonial.Create(request.AuthorName!, request.Place, request.Text!, request.IsPublished!.Value, request.SortOrder!.Value, timeProvider.GetUtcNow());
        db.Testimonials.Add(testimonial);
        await db.SaveChangesAsync(cancellationToken);
        return AdminTestimonialResponse.From(testimonial);
    }

    public async Task<AdminTestimonialResponse> UpdateAsync(Guid id, SaveTestimonialRequest request, CancellationToken cancellationToken)
    {
        var testimonial = await db.Testimonials.SingleOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw new NotFoundException();
        testimonial.Update(request.AuthorName!, request.Place, request.Text!, request.IsPublished!.Value, request.SortOrder!.Value, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return AdminTestimonialResponse.From(testimonial);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var testimonial = await db.Testimonials.SingleOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw new NotFoundException();
        db.Testimonials.Remove(testimonial);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PublicTestimonialsResponse> PublicAsync(CancellationToken cancellationToken) =>
        new(await Ordered(db.Testimonials.AsNoTracking().Where(t => t.IsPublished))
            .Select(t => new PublicTestimonial(t.Id, t.AuthorName, t.Place, t.Text)).ToListAsync(cancellationToken));

    private static IQueryable<Testimonial> Ordered(IQueryable<Testimonial> query) => query.OrderBy(t => t.SortOrder).ThenByDescending(t => t.CreatedAt).ThenBy(t => t.Id);
}
