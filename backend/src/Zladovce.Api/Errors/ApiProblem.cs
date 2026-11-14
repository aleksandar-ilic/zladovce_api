using System.Text.Json.Serialization;
using Zladovce.Application.Cart;
using Zladovce.Domain.Orders;

namespace Zladovce.Api.Errors;

/// <summary>
/// Error response body: RFC 9457 Problem Details with the Zladovce extensions
/// <c>code</c>, <c>errors</c> and <c>traceId</c>. Served as <c>application/problem+json</c>.
/// </summary>
public sealed record ApiProblem
{
    public const string ContentType = "application/problem+json";
    public const string TypeBaseUri = "https://zladovce.com/errors/";

    /// <summary>URI identifying the error kind, derived from <see cref="Code"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Short Serbian title.</summary>
    public required string Title { get; init; }

    /// <summary>HTTP status code.</summary>
    public required int Status { get; init; }

    /// <summary>Stable machine code, for example <c>VALIDATION_FAILED</c>.</summary>
    public required string Code { get; init; }

    /// <summary>Serbian message that can be shown to the user.</summary>
    public required string Detail { get; init; }

    /// <summary>
    /// Field path (camelCase, e.g. <c>variants[0].sku</c>) → messages. Present for validation errors
    /// (400 <c>VALIDATION_FAILED</c>) and for conflicts caused by one field (409 <c>SLUG_TAKEN</c>, <c>SKU_TAKEN</c>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }

    /// <summary>
    /// Checkout conflicts only (409 <c>CART_CHANGED</c>, <c>INSUFFICIENT_STOCK</c>, <c>MINIMUM_ORDER_NOT_MET</c>,
    /// <c>SHIPPING_DISABLED</c>): the cart priced again now, in the shape of <c>POST /api/v1/cart/quote</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CartQuoteResponse? Quote { get; init; }

    /// <summary>409 <c>INVALID_STATUS_TRANSITION</c> only: the statuses the order can move to now (empty for a final status).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OrderStatus>? AllowedNextStatuses { get; init; }

    /// <summary>Identifier to quote when reporting the problem.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; init; }

    public static ApiProblem Create(
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? errors = null) => new()
        {
            Type = TypeBaseUri + code.ToLowerInvariant().Replace('_', '-'),
            Title = title,
            Status = status,
            Code = code,
            Detail = detail,
            Errors = errors,
        };
}
