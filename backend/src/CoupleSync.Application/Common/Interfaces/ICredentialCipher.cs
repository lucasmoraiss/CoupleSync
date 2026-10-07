namespace CoupleSync.Application.Common.Interfaces;

/// <summary>
/// Encrypts the Open Finance credentials before they are stored. The key belongs to the server
/// (OPENFINANCE_ENCRYPTION_KEY); without a valid one the feature is unavailable.
/// </summary>
public interface ICredentialCipher
{
    /// <summary>False when the server has no (valid) key: nothing can be encrypted nor read.</summary>
    bool IsAvailable { get; }

    /// <summary>Encrypts a value; two calls with the same value give different texts. Throws when not available.</summary>
    string Encrypt(string plaintext);

    /// <summary>False when the text was not produced with the current key (or was changed): the value is unreadable.</summary>
    bool TryDecrypt(string ciphertext, out string plaintext);
}
