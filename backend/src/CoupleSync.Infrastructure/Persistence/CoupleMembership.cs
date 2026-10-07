using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// Membership read straight from the membership table on every call (no cache, so a removal takes effect on
/// the very next request). Each check is a single lookup by the table's primary key (group, user). The user's
/// active group (users.couple_id) is never consulted here: it is a preference, not a permission.
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
        return _dbContext.CoupleMembers
            .AsNoTracking()
            .AnyAsync(m => m.CoupleId == coupleId && m.UserId == userId && m.User.IsActive, cancellationToken);
    }

    public Task<bool> IsOwnerAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken)
    {
        return _dbContext.CoupleMembers
            .AsNoTracking()
            .AnyAsync(
                m => m.CoupleId == coupleId
                    && m.UserId == userId
                    && m.Role == CoupleRole.Owner
                    && m.User.IsActive,
                cancellationToken);
    }
}
