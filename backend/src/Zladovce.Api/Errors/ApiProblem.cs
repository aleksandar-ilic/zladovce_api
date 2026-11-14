using System.Text.Json.Serialization;

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

    /// <summary>Field path → messages. Present only for validation errors.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }

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
