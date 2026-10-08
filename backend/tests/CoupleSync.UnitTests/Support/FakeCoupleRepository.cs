using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Couples;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeCoupleRepository : ICoupleRepository
{
    public List<CoupleSync.Domain.Entities.User> Users { get; } = new();

    public List<CoupleSync.Domain.Entities.Couple> Couples { get; } = new();

    public List<(Guid UserId, Guid CoupleId, Guid? RemainingCoupleId)> StoppedDeliveries { get; } = new();

    public List<Guid> RevokedRefreshTokenUserIds { get; } = new();

    /// <summary>The bank connections stored, by who connected and in which group.</summary>
    public List<(Guid UserId, Guid CoupleId, Guid ConnectionId)> BankConnections { get; } = new();

    /// <summary>
    /// What the handlers did, in order: "begin:{user}", "lock:{couple}", "openfinance:{user}:{couple}", "save", "commit".
    /// </summary>
    public List<string> Steps { get; } = new();

    public int SaveChangesCalls { get; private set; }

    public int Commits => Steps.Count(step => step == "commit");

    public Exception? SaveChangesException { get; set; }

    public Task<CoupleSync.Domain.Entities.User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return Task.FromResult(Users.SingleOrDefault(u => u.Id == userId));
    }

    public Task<Dictionary<Guid, string>> GetMemberNamesAsync(Guid coupleId, CancellationToken cancellationToken)
    {
        var couple = Couples.SingleOrDefault(c => c.Id == coupleId);
        return Task.FromResult((couple?.Members ?? []).ToDictionary(m => m.UserId, m => m.User.Name));
    }

    public Task<Guid?> FindIdByJoinCodeAsync(string joinCode, CancellationToken cancellationToken)
    {
        var normalized = joinCode.Trim().ToUpperInvariant();
        return Task.FromResult(Couples.SingleOrDefault(c => c.JoinCode == normalized)?.Id);
    }

    public Task<CoupleSync.Domain.Entities.Couple?> FindByIdWithMembersAsync(Guid coupleId, CancellationToken cancellationToken)
    {
        return Task.FromResult(Couples.SingleOrDefault(c => c.Id == coupleId));
    }

    public Task<IReadOnlyList<UserGroup>> GetGroupsOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        IReadOnlyList<UserGroup> groups = Couples
            .Where(c => c.HasMember(userId))
            .Select(c =>
            {
                var own = c.Members.Single(m => m.UserId == userId);
                return new UserGroup(
                    c.Id,
                    own.Role,
                    own.JoinedAtUtc,
                    c.Members
                        .OrderBy(m => m.JoinedAtUtc)
                        .ThenBy(m => m.UserId)
                        .Select(m => new GroupMemberName(m.UserId, m.User.Name))
                        .ToList());
            })
            .OrderBy(g => g.JoinedAtUtc)
            .ThenBy(g => g.CoupleId)
            .ToList();
        return Task.FromResult(groups);
    }

    public Task<bool> JoinCodeExistsAsync(string joinCode, CancellationToken cancellationToken)
    {
        return Task.FromResult(Couples.Any(c => c.JoinCode == joinCode));
    }

    public Task StopDeliveriesToMemberAsync(Guid userId, Guid coupleId, Guid? remainingCoupleId, CancellationToken cancellationToken)
    {
        StoppedDeliveries.Add((userId, coupleId, remainingCoupleId));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> RemoveOpenFinanceOfMemberAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken)
    {
        Steps.Add($"openfinance:{userId}:{coupleId}");
        IReadOnlyList<Guid> removed = BankConnections
            .Where(c => c.UserId == userId && c.CoupleId == coupleId)
            .Select(c => c.ConnectionId)
            .ToList();
        BankConnections.RemoveAll(c => c.UserId == userId && c.CoupleId == coupleId);
        return Task.FromResult(removed);
    }

    public Task RevokeRefreshTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        RevokedRefreshTokenUserIds.Add(userId);
        return Task.CompletedTask;
    }

    public Task AddCoupleAsync(CoupleSync.Domain.Entities.Couple couple, CancellationToken cancellationToken)
    {
        Couples.Add(couple);
        return Task.CompletedTask;
    }

    public Task<IMembershipChange> BeginMembershipChangeAsync(Guid userId, CancellationToken cancellationToken)
    {
        Steps.Add($"begin:{userId}");
        return Task.FromResult<IMembershipChange>(new RecordedChange(Steps));
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCalls++;
        Steps.Add("save");

        if (SaveChangesException is not null)
        {
            throw SaveChangesException;
        }

        return Task.CompletedTask;
    }

    private sealed class RecordedChange : IMembershipChange
    {
        private readonly List<string> _steps;

        public RecordedChange(List<string> steps) => _steps = steps;

        public Task LockCoupleAsync(Guid coupleId, CancellationToken cancellationToken)
        {
            _steps.Add($"lock:{coupleId}");
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            _steps.Add("commit");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
