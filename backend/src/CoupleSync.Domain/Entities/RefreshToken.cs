namespace CoupleSync.Domain.Entities;

public sealed class RefreshToken
{
    private RefreshToken()
    {
    }

    private RefreshToken(Guid userId, string tokenHash, DateTime expiresAtUtc, DateTime createdAtUtc)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public User? User { get; private set; }

    public static RefreshToken CreateForUser(Guid userId, string tokenHash, DateTime expiresAtUtc, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new ArgumentException("Token hash is required.", nameof(tokenHash));
        }

        return new RefreshToken(userId, tokenHash, expiresAtUtc, createdAtUtc);
    }

    public void Rotate(string tokenHash, DateTime expiresAtUtc, DateTime updatedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new ArgumentException("Token hash is required.", nameof(tokenHash));
        }

        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }
}
