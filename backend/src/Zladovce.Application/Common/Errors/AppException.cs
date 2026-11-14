namespace Zladovce.Application.Common.Errors;

/// <summary>
/// Base type for expected application failures. Each maps to one HTTP status at the API boundary
/// and carries a stable machine <see cref="Code"/> plus Serbian (Latin) text for the user.
/// </summary>
public abstract class AppException(string code, string title, string detail) : Exception($"{code}: {detail}")
{
    /// <summary>Stable machine code, UPPER_SNAKE_CASE (for example <c>NOT_FOUND</c>).</summary>
    public string Code { get; } = code;

    /// <summary>Short Serbian title.</summary>
    public string Title { get; } = title;

    /// <summary>Serbian message that can be shown to the user.</summary>
    public string Detail { get; } = detail;
}

/// <summary>400 – the request is syntactically valid but its data is not acceptable.</summary>
public sealed class RequestValidationException : AppException
{
    public RequestValidationException(IReadOnlyDictionary<string, string[]> errors, string? detail = null)
        : base(ErrorCodes.ValidationFailed, ErrorTexts.ValidationTitle, detail ?? ErrorTexts.ValidationDetail)
    {
        Errors = errors;
    }

    public RequestValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }

    /// <summary>Field path (camelCase) → messages.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>404 – the resource does not exist or must not be revealed.</summary>
public sealed class NotFoundException(
    string? detail = null,
    string code = ErrorCodes.NotFound,
    string? title = null)
    : AppException(code, title ?? ErrorTexts.NotFoundTitle, detail ?? ErrorTexts.NotFoundDetail);

/// <summary>409 – the request conflicts with the current state (stock, status transition, duplicate, concurrent edit).</summary>
public sealed class ConflictException(
    string? detail = null,
    string code = ErrorCodes.Conflict,
    string? title = null)
    : AppException(code, title ?? ErrorTexts.ConflictTitle, detail ?? ErrorTexts.ConflictDetail);

/// <summary>403 – the caller is known but not allowed.</summary>
public sealed class ForbiddenException(
    string? detail = null,
    string code = ErrorCodes.Forbidden,
    string? title = null)
    : AppException(code, title ?? ErrorTexts.ForbiddenTitle, detail ?? ErrorTexts.ForbiddenDetail);

/// <summary>400 – the request cannot be accepted for a reason that is not tied to one field (for example an upload that is not an image).</summary>
public sealed class BadRequestException(
    string? detail = null,
    string code = ErrorCodes.BadRequest,
    string? title = null)
    : AppException(code, title ?? ErrorTexts.BadRequestTitle, detail ?? ErrorTexts.BadRequestDetail);

/// <summary>A variant whose stock does not cover the requested quantity.</summary>
/// <param name="VariantId">The variant.</param>
/// <param name="Requested">Quantity that was asked for.</param>
/// <param name="Available">Quantity on stock at that moment (0 when the variant has no stock record).</param>
public sealed record StockShortage(Guid VariantId, int Requested, int Available);

/// <summary>409 <c>INSUFFICIENT_STOCK</c> – a stock change would take a variant below zero. Nothing was changed.</summary>
public sealed class InsufficientStockException(IReadOnlyList<StockShortage> shortages, string? detail = null)
    : AppException(ErrorCodes.InsufficientStock, ErrorTexts.InsufficientStockTitle, detail ?? ErrorTexts.InsufficientStockDetail)
{
    public IReadOnlyList<StockShortage> Shortages { get; } = shortages;
}
