using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Domain.Entities;

public sealed class User
{
    private User()
    {
    }

    private User(Guid id, string email, string name, string passwordHash, DateTime createdAtUtc)
    {
        Id = id;
        Email = email;
        Name = name;
        PasswordHash = passwordHash;
        CreatedAtUtc = createdAtUtc;
        IsActive = true;
    }

    public Guid Id { get; private set; }

    public Guid? CoupleId { get; private set; }

    public DateTime? CoupleJoinedAtUtc { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    public bool IsActive { get; private set; }

    public Couple? Couple { get; private set; }

    public static User Create(EmailAddress email, string name, string passwordHash, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));
        }

        return new User(Guid.NewGuid(), email.Value, name.Trim(), passwordHash, createdAtUtc);
    }

    public void AssignCouple(Guid coupleId, DateTime coupleJoinedAtUtc)
    {
        if (CoupleId.HasValue)
        {
            throw new InvalidOperationException("User is already assigned to a couple.");
        }

        CoupleId = coupleId;
        CoupleJoinedAtUtc = coupleJoinedAtUtc;
    }
}
