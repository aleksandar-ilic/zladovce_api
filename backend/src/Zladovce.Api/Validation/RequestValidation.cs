using FluentValidation;
using Zladovce.Application.Common.Errors;

namespace Zladovce.Api.Validation;

public static class RequestValidation
{
    /// <summary>
    /// Validates every handler argument that has a registered FluentValidation validator before the
    /// handler runs. Applied once to the <c>/api/v1</c> group, so new endpoints are covered automatically.
    /// Failures become a 400 <c>VALIDATION_FAILED</c> response with the <c>errors</c> map.
    /// </summary>
    public static TBuilder WithRequestValidation<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            var candidates = factoryContext.MethodInfo.GetParameters()
                .Select((parameter, index) => (Index: index, Type: Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType))
                .Where(p => IsRequestModel(p.Type))
                .Select(p => (p.Index, ValidatorType: typeof(IValidator<>).MakeGenericType(p.Type)))
                .ToArray();

            if (candidates.Length == 0)
            {
                return next;
            }

            return async invocationContext =>
            {
                Dictionary<string, List<string>>? errors = null;

                foreach (var (index, validatorType) in candidates)
                {
                    var argument = invocationContext.Arguments[index];
                    if (argument is null ||
                        invocationContext.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator)
                    {
                        continue;
                    }

                    var result = await validator.ValidateAsync(
                        new ValidationContext<object>(argument),
                        invocationContext.HttpContext.RequestAborted);

                    foreach (var failure in result.Errors)
                    {
                        errors ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
                        var key = ToCamelCasePath(failure.PropertyName);
                        if (!errors.TryGetValue(key, out var messages))
                        {
                            errors[key] = messages = [];
                        }

                        messages.Add(failure.ErrorMessage);
                    }
                }

                if (errors is not null)
                {
                    throw new RequestValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
                }

                return await next(invocationContext);
            };
        });

    /// <summary><c>Customer.FirstName</c> → <c>customer.firstName</c>, <c>Items[0].Quantity</c> → <c>items[0].quantity</c>.</summary>
    public static string ToCamelCasePath(string? propertyPath)
    {
        if (string.IsNullOrEmpty(propertyPath))
        {
            return string.Empty;
        }

        var segments = propertyPath.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length > 0 && char.IsUpper(segment[0]))
            {
                segments[i] = char.ToLowerInvariant(segment[0]) + segment[1..];
            }
        }

        return string.Join('.', segments);
    }

    private static bool IsRequestModel(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && type != typeof(string)
        && type.Namespace is { } ns
        && !ns.StartsWith("System", StringComparison.Ordinal)
        && !ns.StartsWith("Microsoft", StringComparison.Ordinal);
}
