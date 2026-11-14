using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Options;
using Zladovce.Application.Notifications;

namespace Zladovce.Infrastructure.Notifications;

/// <summary>Source of the 256-bit AES key that protects tracking codes in outbox rows.</summary>
public interface ITrackingKeyProvider
{
    Task<byte[]> GetKeyAsync(CancellationToken cancellationToken);
}

/// <summary><c>Tracking:TokenEncryptionKey</c> (base64 of 32 bytes): local development and tests.</summary>
public sealed class ConfiguredTrackingKeyProvider(IOptions<TrackingOptions> options) : ITrackingKeyProvider
{
    public Task<byte[]> GetKeyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(TrackingOptions.DecodeKey(options.Value.TokenEncryptionKey)
            ?? throw new InvalidOperationException("Tracking:TokenEncryptionKey must be the base64 encoding of exactly 32 bytes."));
}

/// <summary>
/// The key from Secrets Manager (<c>Tracking:TokenEncryptionKeySecretArn</c>): the secret string is the base64 key, or JSON
/// <c>{ "key": "&lt;base64&gt;" }</c>. Read once per process (Lambda container) and kept in memory; a failed read is retried
/// on the next use.
/// </summary>
public sealed class SecretsManagerTrackingKeyProvider(IOptions<TrackingOptions> options, Func<IAmazonSecretsManager>? clientFactory = null)
    : ITrackingKeyProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _key;

    public async Task<byte[]> GetKeyAsync(CancellationToken cancellationToken)
    {
        if (_key is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_key is null)
            {
                using var client = (clientFactory ?? (() => new AmazonSecretsManagerClient()))();
                var secret = await client.GetSecretValueAsync(
                    new GetSecretValueRequest { SecretId = options.Value.TokenEncryptionKeySecretArn }, cancellationToken);
                _key = ParseSecret(secret.SecretString);
            }

            return _key;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static byte[] ParseSecret(string? secret)
    {
        var value = secret?.Trim();
        if (value is not null && value.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(value);
            value = document.RootElement.TryGetProperty("key", out var key) ? key.GetString() : null;
        }

        return TrackingOptions.DecodeKey(value)
            ?? throw new InvalidOperationException("The tracking key secret must hold the base64 encoding of exactly 32 bytes.");
    }
}

/// <summary>
/// AES-256-GCM. Layout: <c>version (1 byte = 1) | nonce (12) | tag (16) | ciphertext</c>. The outbox row id is the associated
/// data, so a ciphertext copied to another row does not decrypt.
/// </summary>
public sealed class AesGcmTrackingCodeProtector(ITrackingKeyProvider keys) : ITrackingCodeProtector
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    public async Task<byte[]> ProtectAsync(string code, Guid outboxId, CancellationToken cancellationToken) =>
        Protect(await keys.GetKeyAsync(cancellationToken), code, outboxId);

    public async Task<string> UnprotectAsync(byte[] protectedCode, Guid outboxId, CancellationToken cancellationToken) =>
        Unprotect(await keys.GetKeyAsync(cancellationToken), protectedCode, outboxId);

    public static byte[] Protect(byte[] key, string code, Guid outboxId)
    {
        var plaintext = Encoding.UTF8.GetBytes(code);
        var output = new byte[HeaderSize + plaintext.Length];
        output[0] = FormatVersion;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(HeaderSize), output.AsSpan(1 + NonceSize, TagSize), outboxId.ToByteArray());
        return output;
    }

    public static string Unprotect(byte[] key, byte[] data, Guid outboxId)
    {
        if (data.Length < HeaderSize || data[0] != FormatVersion)
        {
            throw new CryptographicException("Unknown tracking code format.");
        }

        var plaintext = new byte[data.Length - HeaderSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(data.AsSpan(1, NonceSize), data.AsSpan(HeaderSize), data.AsSpan(1 + NonceSize, TagSize), plaintext, outboxId.ToByteArray());
        return Encoding.UTF8.GetString(plaintext);
    }
}
