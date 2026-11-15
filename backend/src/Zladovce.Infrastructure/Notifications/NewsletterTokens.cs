using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Zladovce.Application.Notifications;

namespace Zladovce.Infrastructure.Notifications;

/// <summary>
/// HMAC-SHA256 newsletter tokens. The signing key is derived (HKDF-SHA256, info <c>zladovce/newsletter-token/v1</c>) from the
/// tracking-code key, so no further secret has to be provisioned, and the two uses never share a key.
/// Format: <c>base64url(payload).base64url(first 16 bytes of HMAC(payload))</c>; payload = version (1) | purpose (1) |
/// subscriber id (16) | for confirm tokens: confirmation request time in Unix milliseconds (8, big endian).
/// </summary>
public sealed class HmacNewsletterTokens(ITrackingKeyProvider keys) : INewsletterTokens
{
    private const byte FormatVersion = 1;
    private const int MacLength = 16;
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("zladovce/newsletter-token/v1");

    private byte[]? _key;

    public async Task<string> CreateAsync(NewsletterToken token, CancellationToken cancellationToken)
    {
        var payload = new byte[token.Purpose == NewsletterTokenPurpose.Confirm ? 26 : 18];
        payload[0] = FormatVersion;
        payload[1] = token.Purpose == NewsletterTokenPurpose.Confirm ? (byte)'c' : (byte)'u';
        token.SubscriberId.TryWriteBytes(payload.AsSpan(2, 16));
        if (token.Purpose == NewsletterTokenPurpose.Confirm)
        {
            var issued = token.IssuedAt ?? throw new ArgumentException("A confirm token needs the confirmation time.", nameof(token));
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(18, 8), issued.ToUnixTimeMilliseconds());
        }

        var mac = HMACSHA256.HashData(await KeyAsync(cancellationToken), payload).AsSpan(0, MacLength);
        return $"{Base64Url(payload)}.{Base64Url(mac)}";
    }

    public async Task<NewsletterToken?> ReadAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
        {
            return null;
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 2 || FromBase64Url(parts[0]) is not { } payload || FromBase64Url(parts[1]) is not { Length: MacLength } mac)
        {
            return null;
        }

        var expected = HMACSHA256.HashData(await KeyAsync(cancellationToken), payload).AsSpan(0, MacLength);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac) || payload.Length < 18 || payload[0] != FormatVersion)
        {
            return null;
        }

        var id = new Guid(payload.AsSpan(2, 16));
        return (payload[1], payload.Length) switch
        {
            ((byte)'c', 26) => new NewsletterToken(
                NewsletterTokenPurpose.Confirm, id, DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(18, 8)))),
            ((byte)'u', 18) => new NewsletterToken(NewsletterTokenPurpose.Unsubscribe, id, null),
            _ => null,
        };
    }

    private async Task<byte[]> KeyAsync(CancellationToken cancellationToken) =>
        _key ??= HKDF.DeriveKey(HashAlgorithmName.SHA256, await keys.GetKeyAsync(cancellationToken), 32, salt: [], info: Info);

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string value)
    {
        if (value.Length == 0 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            return null;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
