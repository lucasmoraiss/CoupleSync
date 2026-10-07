namespace CoupleSync.Domain.Entities;

public enum CoupleRole
{
    Owner = 1,
    Member = 2
}

/// <summary>
/// One user's membership of one group. This row, and nothing else, is what gives a user access to a group's
/// data; a user may hold several (see <see cref="User.MaxGroups"/>).
/// </summary>
public sealed class CoupleMember
{
    private CoupleMember()
    {
    }

    internal CoupleMember(Guid coupleId, User user, CoupleRole role, DateTime joinedAtUtc)
    {
        CoupleId = coupleId;
        UserId = user.Id;
        User = user;
        Role = role;
        JoinedAtUtc = joinedAtUtc;
    }

    public Guid CoupleId { get; private set; }

    public Guid UserId { get; private set; }

    public CoupleRole Role { get; private set; }

    public DateTime JoinedAtUtc { get; private set; }

    public User User { get; private set; } = null!;

    internal void ChangeRole(CoupleRole role)
    {
        Role = role;
    }
}
