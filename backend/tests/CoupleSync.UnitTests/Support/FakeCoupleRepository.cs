using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeCoupleRepository : ICoupleRepository
{
    public List<CoupleSync.Domain.Entities.User> Users { get; } = new();

    public List<CoupleSync.Domain.Entities.Couple> Couples { get; } = new();

    public int SaveChangesCalls { get; private set; }

    public Exception? SaveChangesException { get; set; }

    public Task<CoupleSync.Domain.Entities.User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return Task.FromResult(Users.SingleOrDefault(u => u.Id == userId));
    }

    public Task<CoupleSync.Domain.Entities.Couple?> FindByJoinCodeAsync(string joinCode, CancellationToken cancellationToken)
    {
        var normalized = joinCode.Trim().ToUpperInvariant();
        return Task.FromResult(Couples.SingleOrDefault(c => c.JoinCode == normalized));
    }

    public Task<CoupleSync.Domain.Entities.Couple?> FindByIdWithMembersAsync(Guid coupleId, CancellationToken cancellationToken)
    {
        return Task.FromResult(Couples.SingleOrDefault(c => c.Id == coupleId));
    }

    public Task<bool> JoinCodeExistsAsync(string joinCode, CancellationToken cancellationToken)
    {
        return Task.FromResult(Couples.Any(c => c.JoinCode == joinCode));
    }

    public Task AddCoupleAsync(CoupleSync.Domain.Entities.Couple couple, CancellationToken cancellationToken)
    {
        Couples.Add(couple);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveChangesCalls++;

        if (SaveChangesException is not null)
        {
            throw SaveChangesException;
        }

        return Task.CompletedTask;
    }
}
