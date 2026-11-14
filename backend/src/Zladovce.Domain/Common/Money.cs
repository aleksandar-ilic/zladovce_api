namespace Zladovce.Domain.Common;

/// <summary>Money helpers (RSD, two decimals).</summary>
public static class Money
{
    public const string Currency = "RSD";

    /// <summary>Rounds to two decimals, halves away from zero.</summary>
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Unit price times quantity, rounded to two decimals.</summary>
    public static decimal LineTotal(decimal unitPrice, int quantity)
    {
        if (quantity < 0)
        {
            throw new DomainException("Quantity cannot be negative.");
        }

        return Round(unitPrice * quantity);
    }
}
