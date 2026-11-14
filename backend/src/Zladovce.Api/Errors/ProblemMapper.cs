using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zladovce.Application.Common.Errors;
using Zladovce.Domain.Common;

namespace Zladovce.Api.Errors;

/// <summary>The single place where exceptions and bare status codes become <see cref="ApiProblem"/> responses.</summary>
public static class ProblemMapper
{
    public static ApiProblem FromException(Exception exception) => exception switch
    {
        RequestValidationException e => ApiProblem.Create(StatusCodes.Status400BadRequest, e.Code, e.Title, e.Detail, e.Errors),
        NotFoundException e => ApiProblem.Create(StatusCodes.Status404NotFound, e.Code, e.Title, e.Detail),
        ConflictException e => ApiProblem.Create(StatusCodes.Status409Conflict, e.Code, e.Title, e.Detail),
        ForbiddenException e => ApiProblem.Create(StatusCodes.Status403Forbidden, e.Code, e.Title, e.Detail),
        BadRequestException e => ApiProblem.Create(StatusCodes.Status400BadRequest, e.Code, e.Title, e.Detail),
        InsufficientStockException e => ApiProblem.Create(StatusCodes.Status409Conflict, e.Code, e.Title, e.Detail),

        // A domain invariant refused the data. The developer message is logged, not returned.
        DomainException => ApiProblem.Create(
            StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed, ErrorTexts.ValidationTitle, ErrorTexts.ValidationDetail),

        DbUpdateConcurrencyException => ApiProblem.Create(
            StatusCodes.Status409Conflict, ErrorCodes.ConcurrentEdit, ErrorTexts.ConcurrentEditTitle, ErrorTexts.ConcurrentEditDetail),

        // A unique index refused a write that passed the checks of the service (two administrators at the same moment).
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique } =>
            FromUniqueViolation(unique.ConstraintName),

        // Malformed JSON, wrong content type, body too large: reported by the framework while binding.
        BadHttpRequestException e => FromStatusCode(e.StatusCode is >= 400 and < 500 ? e.StatusCode : StatusCodes.Status400BadRequest),

        ServiceNotReadyException => FromStatusCode(StatusCodes.Status503ServiceUnavailable),

        _ => FromStatusCode(StatusCodes.Status500InternalServerError),
    };

    /// <summary>Whether the exception is an expected outcome (logged without a stack trace) rather than a fault.</summary>
    public static bool IsExpected(Exception exception) =>
        exception is AppException or DomainException or DbUpdateConcurrencyException or BadHttpRequestException
            or ServiceNotReadyException
            or DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } };

    private static ApiProblem FromUniqueViolation(string? constraintName) => constraintName switch
    {
        "ix_categories_slug" or "ix_products_slug" =>
            ApiProblem.Create(StatusCodes.Status409Conflict, ErrorCodes.SlugTaken, ErrorTexts.SlugTakenTitle, ErrorTexts.SlugTakenDetail),
        "ix_products_sku" or "ix_product_variants_sku" =>
            ApiProblem.Create(StatusCodes.Status409Conflict, ErrorCodes.SkuTaken, ErrorTexts.SkuTakenTitle, ErrorTexts.SkuTakenDetail),
        "pk_product_images" =>
            ApiProblem.Create(StatusCodes.Status409Conflict, ErrorCodes.ImageAlreadyAttached, ErrorTexts.ImageAlreadyAttachedTitle, ErrorTexts.ImageAlreadyAttachedDetail),
        _ => FromStatusCode(StatusCodes.Status409Conflict),
    };

    public static ApiProblem FromStatusCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => ApiProblem.Create(statusCode, ErrorCodes.BadRequest, ErrorTexts.BadRequestTitle, ErrorTexts.BadRequestDetail),
        StatusCodes.Status401Unauthorized => ApiProblem.Create(statusCode, ErrorCodes.Unauthorized, ErrorTexts.UnauthorizedTitle, ErrorTexts.UnauthorizedDetail),
        StatusCodes.Status403Forbidden => ApiProblem.Create(statusCode, ErrorCodes.Forbidden, ErrorTexts.ForbiddenTitle, ErrorTexts.ForbiddenDetail),
        StatusCodes.Status404NotFound => ApiProblem.Create(statusCode, ErrorCodes.NotFound, ErrorTexts.NotFoundTitle, ErrorTexts.NotFoundDetail),
        StatusCodes.Status405MethodNotAllowed => ApiProblem.Create(statusCode, ErrorCodes.MethodNotAllowed, ErrorTexts.MethodNotAllowedTitle, ErrorTexts.MethodNotAllowedDetail),
        StatusCodes.Status409Conflict => ApiProblem.Create(statusCode, ErrorCodes.Conflict, ErrorTexts.ConflictTitle, ErrorTexts.ConflictDetail),
        StatusCodes.Status413PayloadTooLarge => ApiProblem.Create(statusCode, ErrorCodes.PayloadTooLarge, ErrorTexts.PayloadTooLargeTitle, ErrorTexts.PayloadTooLargeDetail),
        StatusCodes.Status415UnsupportedMediaType => ApiProblem.Create(statusCode, ErrorCodes.UnsupportedMediaType, ErrorTexts.UnsupportedMediaTypeTitle, ErrorTexts.UnsupportedMediaTypeDetail),
        StatusCodes.Status429TooManyRequests => ApiProblem.Create(statusCode, ErrorCodes.RateLimited, ErrorTexts.RateLimitedTitle, ErrorTexts.RateLimitedDetail),
        StatusCodes.Status503ServiceUnavailable => ApiProblem.Create(statusCode, ErrorCodes.ServiceUnavailable, ErrorTexts.ServiceUnavailableTitle, ErrorTexts.ServiceUnavailableDetail),
        >= 400 and < 500 => ApiProblem.Create(statusCode, ErrorCodes.BadRequest, ErrorTexts.BadRequestTitle, ErrorTexts.BadRequestDetail),
        _ => ApiProblem.Create(statusCode, ErrorCodes.InternalError, ErrorTexts.InternalErrorTitle, ErrorTexts.InternalErrorDetail),
    };
}

/// <summary>Raised by the readiness endpoint when the database cannot be reached; mapped to 503.</summary>
public sealed class ServiceNotReadyException() : Exception("The database is not reachable.");
