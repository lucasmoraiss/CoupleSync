using CoupleSync.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// Membership read straight from the database on every call (no cache, so a removal takes effect on the
/// very next request). Each check is a single lookup by primary key. Today a user belongs to at most one
/// group (users.couple_id); a user-to-group table with roles replaces these two queries and nothing else.
/// </summary>
public sealed class CoupleMembership : ICoupleMembership
{
    private readonly AppDbContext _dbContext;

    public CoupleMembership(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> IsMemberAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken)
    {
        return _dbContext.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.IsActive && u.CoupleId == coupleId, cancellationToken);
    }

    public Task<bool> IsOwnerAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken)
    {
        return _dbContext.Couples
            .AsNoTracking()
            .AnyAsync(
                c => c.Id == coupleId
                    && c.OwnerUserId == userId
                    && c.Members.Any(m => m.Id == userId && m.IsActive),
                cancellationToken);
    }
}
