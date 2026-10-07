using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Infrastructure.Security;

public sealed class BCryptPasswordHasher : IPasswordHasher
{
    // Computed once, at the same work factor HashPassword uses.
    private static readonly Lazy<string> DummyHashValue =
        new(() => BCrypt.Net.BCrypt.HashPassword("couplesync-dummy-password-for-timing"));

    public string DummyHash => DummyHashValue.Value;

    public string HashPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        return BCrypt.Net.BCrypt.HashPassword(password);
    }

    public bool VerifyPassword(string password, string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(passwordHash))
        {
            return false;
        }

        return BCrypt.Net.BCrypt.Verify(password, passwordHash);
    }
}
