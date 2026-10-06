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
        IssueWindowStartedAtUtc = createdAtUtc;
        IssueCount = 1;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Purpose { get; private set; } = string.Empty;

    public string CodeHash { get; private set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Verification attempts already spent on this code (right or wrong).</summary>
    public int Attempts { get; private set; }

    /// <summary>Start of the current hour-long window in which codes of this purpose were e-mailed to the user.</summary>
    public DateTime IssueWindowStartedAtUtc { get; private set; }

    /// <summary>Codes e-mailed in the current window. Kept in the database so a restart does not grant a fresh budget.</summary>
    public int IssueCount { get; private set; }

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

    /// <summary>True when another code in the current window would exceed <paramref name="maxPerWindow"/>.</summary>
    public bool HasReachedIssueLimit(DateTime now, int maxPerWindow, TimeSpan window) =>
        now - IssueWindowStartedAtUtc < window && IssueCount >= maxPerWindow;

    /// <summary>Replaces the live code in place (the previous one stops working, attempts start over) and counts the issue.</summary>
    public void Reissue(string codeHash, DateTime expiresAtUtc, DateTime now, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(codeHash))
        {
            throw new ArgumentException("O código é obrigatório.", nameof(codeHash));
        }

        if (now - IssueWindowStartedAtUtc >= window)
        {
            IssueWindowStartedAtUtc = now;
            IssueCount = 1;
        }
        else
        {
            IssueCount++;
        }

        CodeHash = codeHash;
        ExpiresAtUtc = expiresAtUtc;
        CreatedAtUtc = now;
        Attempts = 0;
    }

    public bool IsExpired(DateTime now) => ExpiresAtUtc <= now;

    /// <summary>In-memory twin of the atomic increment the repository does in SQL (used by test doubles).</summary>
    public void RegisterAttempt() => Attempts++;
}
