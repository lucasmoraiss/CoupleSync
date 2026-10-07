using System.Security.Cryptography;
using System.Text;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Security;

/// <summary>
/// Six-digit codes from <see cref="RandomNumberGenerator"/>, stored as HMAC-SHA256 keyed with the server JWT secret and
/// bound to user and purpose. A plain hash of a six-digit number is trivially reversible; the key makes a leaked
/// database row useless on its own.
/// </summary>
public sealed class VerificationCodeService : IVerificationCodeService
{
    private readonly byte[] _key;

    public VerificationCodeService(IOptions<JwtOptions> jwtOptions)
    {
        _key = Encoding.UTF8.GetBytes("couplesync-email-code:" + jwtOptions.Value.Secret);
    }

    public string Generate() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public string Hash(Guid userId, string purpose, string code)
    {
        var payload = Encoding.UTF8.GetBytes($"{userId:N}|{purpose}|{code}");
        return Convert.ToHexString(HMACSHA256.HashData(_key, payload)).ToLowerInvariant();
    }

    public bool Verify(Guid userId, string purpose, string code, string expectedHash)
    {
        var actual = Encoding.ASCII.GetBytes(Hash(userId, purpose, code));
        var expected = Encoding.ASCII.GetBytes(expectedHash ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
