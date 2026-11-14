using System.Text;

namespace Zladovce.Domain.Common;

/// <summary>Serbian telephone numbers, stored in E.164 (<c>+381…</c>).</summary>
public static class SerbianPhone
{
    public const string CountryCode = "381";

    /// <summary>Length of the national significant number (without the leading 0 or the country code).</summary>
    public const int MinNationalLength = 8;
    public const int MaxNationalLength = 10;

    /// <summary>
    /// Normalises a Serbian number to E.164. Accepted: <c>+381 64 123 4567</c>, <c>00381641234567</c>,
    /// <c>+381 (0)64 123 4567</c>, <c>064/123-4567</c>, <c>011 123 4567</c>. Spaces, <c>-</c>, <c>/</c>, <c>.</c> and parentheses
    /// are ignored. The national number has 8–10 digits and does not start with 0. Returns null when the value is not such a number.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var trimmed = value.Trim();
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if (c == '+' && i == 0)
            {
                builder.Append(c);
            }
            else if (c is not (' ' or '-' or '/' or '.' or '(' or ')'))
            {
                return null;
            }
        }

        var digits = builder.ToString();
        string national;
        if (digits.StartsWith("+" + CountryCode, StringComparison.Ordinal) || digits.StartsWith("00" + CountryCode, StringComparison.Ordinal))
        {
            national = digits[(digits[0] == '+' ? CountryCode.Length + 1 : CountryCode.Length + 2)..];

            // "+381 (0)64 …": the trunk zero written after the country code.
            if (national.StartsWith('0'))
            {
                national = national[1..];
            }
        }
        else if (digits.StartsWith('0') && !digits.StartsWith("00", StringComparison.Ordinal))
        {
            national = digits[1..];
        }
        else
        {
            return null;
        }

        if (national.Length is < MinNationalLength or > MaxNationalLength || national[0] == '0' || national.Any(c => c is < '0' or > '9'))
        {
            return null;
        }

        return "+" + CountryCode + national;
    }
}

/// <summary>Serbian postal codes: five digits.</summary>
public static class SerbianPostalCode
{
    public static bool IsValid(string? value) =>
        value is { Length: 5 } && value.All(c => c is >= '0' and <= '9');
}
