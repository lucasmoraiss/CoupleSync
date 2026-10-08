using System.Security.Cryptography;
using System.Text;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Infrastructure.Integrations.Pluggy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Security;

/// <summary>
/// AES-256-GCM with a random nonce per value. Stored text: Base64 of <c>version(1) | nonce(12) | tag(16) | ciphertext</c>.
/// The key is the server's OPENFINANCE_ENCRYPTION_KEY (Base64 of 32 bytes); absent or malformed, the cipher is
/// unavailable and the API still starts.
/// </summary>
public sealed class AesGcmCredentialCipher : ICredentialCipher
{
    private const byte FormatVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    private byte[]? _key;

    public AesGcmCredentialCipher(IOptions<OpenFinanceOptions> options, ILogger<AesGcmCredentialCipher> logger)
    {
        var configured = options.Value.EncryptionKey;
        if (string.IsNullOrWhiteSpace(configured)) return;

        _key = ParseKey(configured);
        if (_key is null)
        {
            // The value itself is never written anywhere.
            logger.LogWarning(
                "{Variable} is set but is not the Base64 of {KeySize} bytes: Open Finance stays unavailable.",
                OpenFinanceOptions.EncryptionKeyVariable, KeySize);
            return;
        }

        // "Available" has to mean that this host can really encrypt and read back (AES-GCM comes from the
        // operating system's crypto library): found out here, not on the first person's credentials.
        if (!AesGcm.IsSupported || !RoundTrips())
        {
            _key = null;
            logger.LogWarning(
                "AES-GCM is not usable on this host: Open Finance stays unavailable although {Variable} is set.",
                OpenFinanceOptions.EncryptionKeyVariable);
        }
    }

    private bool RoundTrips()
    {
        const string probe = "couplesync";
        try
        {
            return TryDecrypt(Encrypt(probe), out var back) && back == probe;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public bool IsAvailable => _key is not null;

    public string Encrypt(string plaintext)
    {
        if (_key is null)
            throw new InvalidOperationException("The Open Finance encryption key is not configured.");

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[HeaderSize + plain.Length];
        output[0] = FormatVersion;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(HeaderSize), output.AsSpan(1 + NonceSize, TagSize));
        return Convert.ToBase64String(output);
    }

    public bool TryDecrypt(string ciphertext, out string plaintext)
    {
        plaintext = string.Empty;
        if (_key is null || string.IsNullOrEmpty(ciphertext)) return false;

        var input = new byte[ciphertext.Length];
        if (!Convert.TryFromBase64String(ciphertext, input, out var length)) return false;
        if (length < HeaderSize || input[0] != FormatVersion) return false;

        var plain = new byte[length - HeaderSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(
                input.AsSpan(1, NonceSize),
                input.AsSpan(HeaderSize, plain.Length),
                input.AsSpan(1 + NonceSize, TagSize),
                plain);
        }
        catch (CryptographicException)
        {
            // Another key, or a text that was changed: unreadable.
            return false;
        }

        plaintext = Encoding.UTF8.GetString(plain);
        return true;
    }

    private static byte[]? ParseKey(string configured)
    {
        var text = configured.Trim();
        var buffer = new byte[text.Length];
        return Convert.TryFromBase64String(text, buffer, out var length) && length == KeySize
            ? buffer[..length]
            : null;
    }
}
