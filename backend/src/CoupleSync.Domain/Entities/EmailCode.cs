namespace CoupleSync.Domain.Entities;

/// <summary>What a one-time e-mail code is for. Stored as text in <c>email_codes.purpose</c>.</summary>
public static class EmailCodePurpose
{
    public const string EmailVerification = "email_verification";
    public const string PasswordReset = "password_reset";
}

/// <summary>
/// A six-digit code sent by e-mail. Only a keyed hash is stored. A user has at most one code per purpose:
/// asking for a new one replaces (invalidates) the previous one. The verification attempts are counted on
/// the server, per code; the row is deleted when the code is used.
/// </summary>
public sealed class EmailCode
{
    private EmailCode()
    {
    }

    private EmailCode(Guid userId, string purpose, string codeHash, DateTime expiresAtUtc, DateTime createdAtUtc)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        Purpose = purpose;
        CodeHash = codeHash;
        ExpiresAtUtc = expiresAtUtc;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Purpose { get; private set; } = string.Empty;

    public string CodeHash { get; private set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Verification attempts already spent on this code (right or wrong).</summary>
    public int Attempts { get; private set; }

    public User? User { get; private set; }

    public static EmailCode Create(Guid userId, string purpose, string codeHash, DateTime expiresAtUtc, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentException("A finalidade é obrigatória.", nameof(purpose));
        }

        if (string.IsNullOrWhiteSpace(codeHash))
        {
            throw new ArgumentException("O código é obrigatório.", nameof(codeHash));
        }

        return new EmailCode(userId, purpose, codeHash, expiresAtUtc, createdAtUtc);
    }

    public bool IsExpired(DateTime now) => ExpiresAtUtc <= now;

    /// <summary>In-memory twin of the atomic increment the repository does in SQL (used by test doubles).</summary>
    public void RegisterAttempt() => Attempts++;
}
