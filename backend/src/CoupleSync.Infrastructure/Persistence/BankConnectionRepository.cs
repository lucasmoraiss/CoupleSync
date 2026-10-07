using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

/// <summary>
/// Every query names the group explicitly, on top of the global filter of <see cref="AppDbContext"/>: the filter is
/// off when there is no request (background work), the explicit condition never is.
/// </summary>
public sealed class BankConnectionRepository : IBankConnectionRepository
{
    private readonly AppDbContext _dbContext;

    public BankConnectionRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<BankConnection?> FindConnectionAsync(Guid id, Guid coupleId, CancellationToken ct)
        => _dbContext.BankConnections.FirstOrDefaultAsync(c => c.Id == id && c.CoupleId == coupleId, ct);

    public Task<BankConnection?> FindConnectionOfUserAsync(Guid coupleId, Guid userId, CancellationToken ct)
        => _dbContext.BankConnections.FirstOrDefaultAsync(c => c.CoupleId == coupleId && c.UserId == userId, ct);

    public async Task<IReadOnlyList<BankConnection>> GetConnectionsAsync(Guid coupleId, CancellationToken ct)
        => await _dbContext.BankConnections.AsNoTracking().Where(c => c.CoupleId == coupleId).ToListAsync(ct);

    public async Task<IReadOnlyList<BankItem>> GetItemsAsync(Guid coupleId, CancellationToken ct)
        => await _dbContext.BankItems.AsNoTracking().Where(i => i.CoupleId == coupleId).OrderBy(i => i.CreatedAtUtc).ToListAsync(ct);

    public Task<BankItem?> FindItemAsync(Guid id, Guid coupleId, CancellationToken ct)
        => _dbContext.BankItems.FirstOrDefaultAsync(i => i.Id == id && i.CoupleId == coupleId, ct);

    public Task<BankItem?> FindItemByPluggyIdAsync(string pluggyItemId, Guid coupleId, CancellationToken ct)
        => _dbContext.BankItems.FirstOrDefaultAsync(i => i.PluggyItemId == pluggyItemId && i.CoupleId == coupleId, ct);

    public async Task<IReadOnlyList<BankAccount>> GetAccountsAsync(Guid coupleId, CancellationToken ct)
        => await _dbContext.BankAccounts.AsNoTracking().Where(a => a.CoupleId == coupleId).ToListAsync(ct);

    public async Task<IReadOnlyList<BankAccount>> GetAccountsOfItemAsync(Guid itemId, Guid coupleId, CancellationToken ct)
        => await _dbContext.BankAccounts.Where(a => a.ItemId == itemId && a.CoupleId == coupleId).ToListAsync(ct);

    public Task<BankAccount?> FindAccountAsync(Guid id, Guid coupleId, CancellationToken ct)
        => _dbContext.BankAccounts.FirstOrDefaultAsync(a => a.Id == id && a.CoupleId == coupleId, ct);

    public Task AddConnectionAsync(BankConnection connection, CancellationToken ct)
        => _dbContext.BankConnections.AddAsync(connection, ct).AsTask();

    public Task AddItemAsync(BankItem item, CancellationToken ct)
        => _dbContext.BankItems.AddAsync(item, ct).AsTask();

    public Task AddAccountAsync(BankAccount account, CancellationToken ct)
        => _dbContext.BankAccounts.AddAsync(account, ct).AsTask();

    public void RequireSameCredentialsOnSave(BankConnection connection)
        // An UPDATE of the row carries the concurrency token (the stored secret) in its WHERE: writing a column
        // back, changed or not, is what makes the save check it.
        => _dbContext.Entry(connection).Property(c => c.UpdatedAtUtc).IsModified = true;

    public Task ReloadConnectionAsync(BankConnection connection, CancellationToken ct)
        => _dbContext.Entry(connection).ReloadAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct)
        => DbSaveTranslator.SaveAsync(_dbContext, ct);
}
