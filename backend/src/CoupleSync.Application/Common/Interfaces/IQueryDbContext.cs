using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Application.Common.Interfaces;

/// <summary>
/// Read-only projection interface for query handlers that need direct DbSet access
/// (e.g., for JOIN-like queries not covered by repositories).
/// Only add members that are truly needed by Application layer queries.
/// </summary>
public interface IQueryDbContext
{
    DbSet<User> Users { get; }
    DbSet<Goal> Goals { get; }
    DbSet<Transaction> Transactions { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
