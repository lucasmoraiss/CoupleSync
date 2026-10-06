namespace CoupleSync.Application.Common.Interfaces;

public interface IVerificationCodeService
{
    /// <summary>A uniformly random six-digit code (leading zeros kept), from a cryptographic RNG.</summary>
    string Generate();

    /// <summary>Keyed hash of the code, bound to the user and purpose so it cannot be replayed elsewhere.</summary>
    string Hash(Guid userId, string purpose, string code);

    /// <summary>Constant-time comparison of the typed code with the stored hash.</summary>
    bool Verify(Guid userId, string purpose, string code, string expectedHash);
}
