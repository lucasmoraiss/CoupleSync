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

    private Couple(Guid id, string joinCode, DateTime createdAtUtc)
    {
        Id = id;
        JoinCode = joinCode;
        CreatedAtUtc = createdAtUtc;
        Status = CoupleStatus.Active;
    }

    public Guid Id { get; private set; }

    public string JoinCode { get; private set; } = string.Empty;

    public CoupleStatus Status { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public IReadOnlyCollection<User> Members => _members;

    public static Couple Create(string joinCode, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(joinCode))
        {
            throw new ArgumentException("O código de convite é obrigatório.", nameof(joinCode));
        }

        var normalizedJoinCode = joinCode.Trim().ToUpperInvariant();

        if (normalizedJoinCode.Length != 6 || normalizedJoinCode.Any(x => !char.IsAsciiLetterOrDigit(x)))
        {
            throw new ArgumentException("O código de convite deve ter 6 caracteres alfanuméricos.", nameof(joinCode));
        }

        return new Couple(Guid.NewGuid(), normalizedJoinCode, createdAtUtc);
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
    }
}