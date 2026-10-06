namespace CoupleSync.Domain.Entities;

public enum CoupleStatus
{
    Active = 1,
    Dissolved = 2
}

public sealed class Couple
{
    private readonly List<User> _members = new();

    private Couple()
    {
    }

    /// <summary>How long an invite code stays valid after it is generated.</summary>
    public static readonly TimeSpan JoinCodeValidity = TimeSpan.FromDays(7);

    private Couple(Guid id, string joinCode, DateTime createdAtUtc)
    {
        Id = id;
        JoinCode = joinCode;
        CreatedAtUtc = createdAtUtc;
        JoinCodeExpiresAtUtc = createdAtUtc + JoinCodeValidity;
        Status = CoupleStatus.Active;
    }

    public Guid Id { get; private set; }

    public string JoinCode { get; private set; } = string.Empty;

    public CoupleStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime JoinCodeExpiresAtUtc { get; private set; }

    /// <summary>The member who manages the group (removes members, renews the code); null only for a group with no member left.</summary>
    public Guid? OwnerUserId { get; private set; }

    public IReadOnlyCollection<User> Members => _members;

    public static Couple Create(string joinCode, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(joinCode))
        {
            throw new ArgumentException("O código de convite é obrigatório.", nameof(joinCode));
        }

        return new Couple(Guid.NewGuid(), NormalizeJoinCode(joinCode), createdAtUtc);
    }

    public bool IsJoinCodeExpired(DateTime nowUtc) => nowUtc >= JoinCodeExpiresAtUtc;

    /// <summary>Replaces the invite code; the previous one stops working at once and the new one is valid for 7 days.</summary>
    public void RegenerateJoinCode(string joinCode, DateTime nowUtc)
    {
        JoinCode = NormalizeJoinCode(joinCode);
        JoinCodeExpiresAtUtc = nowUtc + JoinCodeValidity;
    }

    /// <summary>
    /// Takes the member out of the group. The owner leaving passes ownership to the oldest remaining member;
    /// the last member leaving leaves a group nobody can reach (its code is expired, its data is kept).
    /// </summary>
    public void RemoveMember(User user, DateTime nowUtc)
    {
        if (user is null)
        {
            throw new ArgumentNullException(nameof(user));
        }

        var member = _members.SingleOrDefault(x => x.Id == user.Id)
            ?? throw new InvalidOperationException("O usuário não faz parte deste casal.");

        _members.Remove(member);
        member.LeaveCouple();

        if (_members.Count == 0)
        {
            OwnerUserId = null;
            JoinCodeExpiresAtUtc = nowUtc;
        }
        else if (OwnerUserId == member.Id)
        {
            OwnerUserId = OldestMember().Id;
        }
    }

    // Same rule as the migration that backfills owners: earliest join (user creation date when unknown), then id.
    private User OldestMember() => _members
        .OrderBy(x => x.CoupleJoinedAtUtc ?? x.CreatedAtUtc)
        .ThenBy(x => x.Id)
        .First();

    private static string NormalizeJoinCode(string joinCode)
    {
        if (string.IsNullOrWhiteSpace(joinCode))
        {
            throw new ArgumentException("O código de convite é obrigatório.", nameof(joinCode));
        }

        var normalized = joinCode.Trim().ToUpperInvariant();

        // Codes issued before the 8-character format stay valid (6 characters) until they expire.
        if (normalized.Length is not (6 or 8) || normalized.Any(x => !char.IsAsciiLetterOrDigit(x)))
        {
            throw new ArgumentException("O código de convite deve ter 6 ou 8 caracteres alfanuméricos.", nameof(joinCode));
        }

        return normalized;
    }

    public void AddMember(User user, DateTime joinedAtUtc)
    {
        if (user is null)
        {
            throw new ArgumentNullException(nameof(user));
        }

        if (_members.Any(x => x.Id == user.Id))
        {
            throw new InvalidOperationException("O usuário já faz parte deste casal.");
        }

        user.AssignCouple(Id, joinedAtUtc);
        _members.Add(user);
        OwnerUserId ??= user.Id;
    }
}