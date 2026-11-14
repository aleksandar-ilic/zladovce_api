using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;

namespace Zladovce.Api.Validation;

/// <summary>
/// "Send every field" for request bodies: members declared with the C# <c>required</c> keyword must be present in the
/// JSON (a nullable member may be <c>null</c>, but it must be there).
/// <para>
/// System.Text.Json would reject a missing required member with a bare 400 <c>BAD_REQUEST</c> and no field. Instead,
/// <see cref="Modifier"/> turns its own check off for request models and records which members each deserialized object
/// received; <see cref="CollectMissing"/> (called by the request validation filter) then reports the missing ones as
/// 400 <c>VALIDATION_FAILED</c> with the usual camelCase field paths (<c>isFeatured</c>, <c>variants[0].isActive</c>).
/// The OpenAPI document lists the same members as <c>required</c> (<see cref="IsRequiredMember"/>).
/// </para>
/// </summary>
public static class RequiredMembers
{
    public const string MissingMessage = "Polje je obavezno (pošaljite vrednost, ili null gde je dozvoljeno).";

    // Members received by each deserialized request object. Weak keys: entries disappear with the request objects.
    private static readonly ConditionalWeakTable<object, HashSet<string>> Received = new();

    // Request model type → JSON names of its required members.
    private static readonly ConcurrentDictionary<Type, string[]> RequiredByType = new();

    /// <summary>JSON contract modifier for the HTTP serializer options.</summary>
    public static void Modifier(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !IsRequestModel(typeInfo.Type))
        {
            return;
        }

        var required = new List<string>();
        foreach (var property in typeInfo.Properties)
        {
            if (!property.IsRequired || property.Set is not { } set)
            {
                continue;
            }

            var name = property.Name;
            required.Add(name);
            property.IsRequired = false;
            property.Set = (target, value) =>
            {
                ReceivedBy(target).Add(name);
                set(target, value);
            };
        }

        if (required.Count == 0)
        {
            return;
        }

        RequiredByType[typeInfo.Type] = [.. required];

        // Registers the object before its members are read, so that "{}" is recognised as "everything missing".
        var previous = typeInfo.OnDeserializing;
        typeInfo.OnDeserializing = target =>
        {
            ReceivedBy(target);
            previous?.Invoke(target);
        };
    }

    /// <summary>
    /// Adds "missing" errors for every required member that the JSON of <paramref name="model"/> (or of a nested request
    /// object, at any depth) did not contain. Objects that were not created by the deserializer are not checked.
    /// </summary>
    public static void CollectMissing(object? model, string path, IDictionary<string, List<string>> errors)
    {
        if (model is null || !IsRequestModel(model.GetType()))
        {
            return;
        }

        var type = model.GetType();
        if (RequiredByType.TryGetValue(type, out var required) && Received.TryGetValue(model, out var received))
        {
            foreach (var name in required.Where(name => !received.Contains(name)))
            {
                var key = path.Length == 0 ? name : $"{path}.{name}";
                if (!errors.TryGetValue(key, out var messages))
                {
                    errors[key] = messages = [];
                }

                messages.Add(MissingMessage);
            }
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !MayContainRequestModels(property.PropertyType))
            {
                continue;
            }

            var childPath = (path.Length == 0 ? string.Empty : path + ".") + RequestValidation.ToCamelCasePath(property.Name);
            var value = property.GetValue(model);
            if (value is IEnumerable items and not string)
            {
                var index = 0;
                foreach (var item in items)
                {
                    CollectMissing(item, $"{childPath}[{index++}]", errors);
                }
            }
            else
            {
                CollectMissing(value, childPath, errors);
            }
        }
    }

    /// <summary>Whether the member behind a serializer property is declared with the C# <c>required</c> keyword.</summary>
    public static bool IsRequiredMember(JsonPropertyInfo property) =>
        property.AttributeProvider is MemberInfo member && member.IsDefined(typeof(RequiredMemberAttribute), inherit: true);

    /// <summary>Request bodies are the records of the Application project.</summary>
    public static bool IsRequestModel(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && type.Namespace is { } ns
        && ns.StartsWith("Zladovce.Application", StringComparison.Ordinal);

    private static HashSet<string> ReceivedBy(object target) =>
        Received.GetValue(target, static _ => new HashSet<string>(StringComparer.Ordinal));

    private static bool MayContainRequestModels(Type type)
    {
        if (type == typeof(string) || type.IsValueType)
        {
            return false;
        }

        if (IsRequestModel(type) || type.IsAbstract && type.Namespace?.StartsWith("Zladovce.Application", StringComparison.Ordinal) == true)
        {
            return true;
        }

        var element = type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];

        return element is not null && MayContainRequestModels(element);
    }
}
