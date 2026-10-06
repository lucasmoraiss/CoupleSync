using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Couples;

public sealed record GroupMemberName(Guid UserId, string Name);

/// <summary>One of the user's groups, as the user sees it: their role, when they joined and who is in it.</summary>
public sealed record UserGroup(Guid CoupleId, CoupleRole Role, DateTime JoinedAtUtc, IReadOnlyList<GroupMemberName> Members)
{
    /// <summary>Groups have no name: they are told apart by who else is in them.</summary>
    public string LabelFor(Guid userId) =>
        GroupLabel.For(Members.Where(m => m.UserId != userId).Select(m => m.Name));
}

public static class GroupLabel
{
    public static string For(IEnumerable<string> otherMemberNames)
    {
        var names = otherMemberNames
            .Select(FirstName)
            .Where(name => name.Length > 0)
            .ToList();

        return names.Count switch
        {
            0 => "Grupo só seu",
            1 => $"Grupo com {names[0]}",
            2 => $"Grupo com {names[0]} e {names[1]}",
            _ => $"Grupo com {names[0]}, {names[1]} e mais {names.Count - 2}"
        };
    }

    /// <summary>A push about one group says which, but only for a user who has more than one.</summary>
    public static string PushTitle(string title, Guid coupleId, Guid userId, IReadOnlyList<UserGroup> groupsOfUser)
    {
        if (groupsOfUser.Count < 2)
        {
            return title;
        }

        var group = groupsOfUser.FirstOrDefault(g => g.CoupleId == coupleId);
        return group is null ? title : $"{title} · {group.LabelFor(userId)}";
    }

    private static string FirstName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }
}
