using System.Text.Json.Serialization;

namespace Zladovce.Domain.Catalog;

/// <summary>Unit in which the amount of a variant is expressed. Stored and serialised as the short code.</summary>
public enum MeasurementUnit
{
    [JsonStringEnumMemberName("g")]
    Gram,

    [JsonStringEnumMemberName("kg")]
    Kilogram,

    [JsonStringEnumMemberName("ml")]
    Millilitre,

    [JsonStringEnumMemberName("l")]
    Litre,

    [JsonStringEnumMemberName("kom")]
    Piece,
}

public static class MeasurementUnitCodes
{
    public static readonly IReadOnlyList<string> All = ["g", "kg", "ml", "l", "kom"];

    public static string ToCode(MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.Gram => "g",
        MeasurementUnit.Kilogram => "kg",
        MeasurementUnit.Millilitre => "ml",
        MeasurementUnit.Litre => "l",
        MeasurementUnit.Piece => "kom",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown measurement unit."),
    };

    public static MeasurementUnit FromCode(string code) => code switch
    {
        "g" => MeasurementUnit.Gram,
        "kg" => MeasurementUnit.Kilogram,
        "ml" => MeasurementUnit.Millilitre,
        "l" => MeasurementUnit.Litre,
        "kom" => MeasurementUnit.Piece,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown measurement unit code."),
    };
}
