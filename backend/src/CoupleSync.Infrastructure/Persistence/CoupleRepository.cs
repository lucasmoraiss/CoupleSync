using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class CoupleRepository : ICoupleRepository
{
    private readonly AppDbContext _dbContext;

    public CoupleRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return _dbContext.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
    }

    public Task<Couple?> FindByJoinCodeAsync(string joinCode, CancellationToken cancellationToken)
    {
        var normalizedJoinCode = joinCode.Trim().ToUpperInvariant();

        return _dbContext.Couples
            .Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.JoinCode == normalizedJoinCode, cancellationToken);
    }

    public Task<Couple?> FindByIdWithMembersAsync(Guid coupleId, CancellationToken cancellationToken)
    {
        return _dbContext.Couples
            .Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Id == coupleId, cancellationToken);
    }

    public Task<bool> JoinCodeExistsAsync(string joinCode, CancellationToken cancellationToken)
    {
        var normalizedJoinCode = joinCode.Trim().ToUpperInvariant();
        return _dbContext.Couples.AnyAsync(x => x.JoinCode == normalizedJoinCode, cancellationToken);
    }

    public async Task AddCoupleAsync(Couple couple, CancellationToken cancellationToken)
    {
        await _dbContext.Couples.AddAsync(couple, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _dbContext.SaveChangesAsync(cancellationToken);
    }
}