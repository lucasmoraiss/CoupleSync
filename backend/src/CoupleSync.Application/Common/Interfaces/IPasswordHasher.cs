namespace CoupleSync.Application.Common.Interfaces;

public interface IPasswordHasher
{
    string HashPassword(string password);

    /// <summary>
    /// A valid hash with the same work factor as real ones. Login verifies against it when the e-mail is
    /// unknown, so a missing account costs the same time as a wrong password.
    /// </summary>
    string DummyHash { get; }

    bool VerifyPassword(string password, string passwordHash);
}
