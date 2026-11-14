using System.Security.Cryptography;
using System.Text;

namespace Zladovce.Domain.Tracking;

/// <summary>
/// The internal Zladovce tracking code (A4 "Tracking tokens"): 130 random bits from the system CSPRNG, written as 26 Crockford
/// Base32 characters (<c>0-9 A-Z</c> without <c>I L O U</c>) and shown in groups <c>XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX</c>.
/// Only the SHA-256 hash of the canonical form (26 upper-case characters, no separators) is stored.
/// <para>
/// Input is accepted with or without hyphens and spaces, in any case, and with the Crockford ambiguity mapping
/// (<c>O → 0</c>, <c>I</c> and <c>L → 1</c>). 26 × 5 = 130 bits exactly, so every canonical string is a distinct value and
/// no padding bits exist.
/// </para>
/// </summary>
public static class TrackingCode
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Characters of the canonical code.</summary>
    public const int Length = 26;

    /// <summary>Random bits in a code.</summary>
    public const int Bits = 130;

    /// <summary>Bytes needed to hold 130 bits (the last 6 bits of the last byte are zero).</summary>
    public const int ByteLength = 17;

    /// <summary>Longest input that is examined at all; anything longer is invalid (but still hashed by the lookup).</summary>
    public const int MaxInputLength = 64;

    private static readonly int[] GroupSizes = [5, 5, 5, 5, 6];

    /// <summary>A new random code in canonical form.</summary>
    public static string Generate()
    {
        Span<byte> random = stackalloc byte[ByteLength];
        RandomNumberGenerator.Fill(random);

        // Keep exactly 130 bits: the low 6 bits of the 17th byte are cleared.
        random[ByteLength - 1] &= 0b1100_0000;
        return Encode(random);
    }

    /// <summary>Encodes 17 bytes (130 significant bits, big-endian) as 26 Crockford characters.</summary>
    public static string Encode(ReadOnlySpan<byte> value)
    {
        if (value.Length != ByteLength)
        {
            throw new ArgumentException($"A tracking code value has {ByteLength} bytes.", nameof(value));
        }

        var chars = new char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[ReadBits(value, i * 5)];
        }

        return new string(chars);
    }

    /// <summary>
    /// Decodes user input into the 17-byte value. False for anything that is not 26 Crockford characters after removing
    /// hyphens and whitespace (case-insensitive, <c>O→0</c>, <c>I/L→1</c>).
    /// </summary>
    public static bool TryDecode(string? input, out byte[] value)
    {
        value = [];
        var canonical = Normalize(input);
        if (canonical is null)
        {
            return false;
        }

        var bytes = new byte[ByteLength];
        for (var i = 0; i < Length; i++)
        {
            WriteBits(bytes, i * 5, Alphabet.IndexOf(canonical[i], StringComparison.Ordinal));
        }

        value = bytes;
        return true;
    }

    /// <summary>The canonical form (26 upper-case characters) of user input, or null when it is not a well-formed code.</summary>
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > MaxInputLength)
        {
            return null;
        }

        var builder = new StringBuilder(Length);
        foreach (var raw in input)
        {
            if (raw == '-' || char.IsWhiteSpace(raw))
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };

            if (c > 'Z' || Alphabet.IndexOf(c, StringComparison.Ordinal) < 0 || builder.Length == Length)
            {
                return null;
            }

            builder.Append(c);
        }

        return builder.Length == Length ? builder.ToString() : null;
    }

    /// <summary>Display form <c>XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX</c> of a canonical code.</summary>
    public static string Format(string canonical)
    {
        if (Normalize(canonical) is not { } code)
        {
            throw new ArgumentException("Not a tracking code.", nameof(canonical));
        }

        var parts = new List<string>(GroupSizes.Length);
        var start = 0;
        foreach (var size in GroupSizes)
        {
            parts.Add(code.Substring(start, size));
            start += size;
        }

        return string.Join('-', parts);
    }

    /// <summary>SHA-256 of the canonical code (ASCII). This is what <c>order_tracking_tokens.token_hash</c> holds.</summary>
    public static byte[] Hash(string canonical) => SHA256.HashData(Encoding.ASCII.GetBytes(canonical));

    /// <summary>
    /// The hash used to look up user input. Always computes a SHA-256, also for malformed input (which can never match a
    /// stored hash because a stored hash is always of a canonical code), so a malformed code costs the same work as an
    /// unknown one: hashing plus one database lookup.
    /// </summary>
    public static byte[] LookupHash(string? input)
    {
        if (Normalize(input) is { } canonical)
        {
            return Hash(canonical);
        }

        var raw = input ?? string.Empty;
        if (raw.Length > MaxInputLength)
        {
            raw = raw[..MaxInputLength];
        }

        // Prefixed so it can never collide with the hash of a canonical code.
        return SHA256.HashData(Encoding.UTF8.GetBytes("invalid:" + raw));
    }

    private static int ReadBits(ReadOnlySpan<byte> bytes, int bitOffset)
    {
        var value = 0;
        for (var b = 0; b < 5; b++)
        {
            var bit = bitOffset + b;
            value = (value << 1) | ((bytes[bit / 8] >> (7 - (bit % 8))) & 1);
        }

        return value;
    }

    private static void WriteBits(byte[] bytes, int bitOffset, int value)
    {
        for (var b = 0; b < 5; b++)
        {
            var bit = bitOffset + b;
            if (((value >> (4 - b)) & 1) == 1)
            {
                bytes[bit / 8] |= (byte)(1 << (7 - (bit % 8)));
            }
        }
    }
}

/// <summary>
/// A tracking code of an order (table <c>order_tracking_tokens</c>). An order can have several valid codes: checkout issues
/// one, every status email and "resend confirmation" issue a fresh one. Only the hash is stored; the plain code exists in
/// the create-order response and (encrypted) in the outbox row until the email is sent.
/// </summary>
public sealed class OrderTrackingToken
{
    private OrderTrackingToken()
    {
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }

    /// <summary>SHA-256 of the canonical code, unique.</summary>
    public byte[] TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Set when an administrator revoked the order's codes; a revoked code is "not found".</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Last successful lookup.</summary>
    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>Creates a token with a new random code and returns the plain code (canonical form) next to it.</summary>
    public static (OrderTrackingToken Token, string Code) Issue(Guid orderId, DateTimeOffset now)
    {
        if (orderId == Guid.Empty)
        {
            throw new Common.DomainException("A tracking token needs an order.");
        }

        var code = TrackingCode.Generate();
        var token = new OrderTrackingToken
        {
            Id = Guid.CreateVersion7(now),
            OrderId = orderId,
            TokenHash = TrackingCode.Hash(code),
            CreatedAt = now,
        };
        return (token, code);
    }
}
