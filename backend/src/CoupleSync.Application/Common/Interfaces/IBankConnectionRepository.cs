using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

/// <summary>Open Finance connections, items and accounts. Every read is restricted to the given group.</summary>
public interface IBankConnectionRepository
{
    Task<BankConnection?> FindConnectionAsync(Guid id, Guid coupleId, CancellationToken ct);

    Task<BankConnection?> FindConnectionOfUserAsync(Guid coupleId, Guid userId, CancellationToken ct);

    Task<IReadOnlyList<BankConnection>> GetConnectionsAsync(Guid coupleId, CancellationToken ct);

    Task<IReadOnlyList<BankItem>> GetItemsAsync(Guid coupleId, CancellationToken ct);

    Task<BankItem?> FindItemAsync(Guid id, Guid coupleId, CancellationToken ct);

    Task<BankItem?> FindItemByPluggyIdAsync(string pluggyItemId, Guid coupleId, CancellationToken ct);

    Task<IReadOnlyList<BankAccount>> GetAccountsAsync(Guid coupleId, CancellationToken ct);

    Task<IReadOnlyList<BankAccount>> GetAccountsOfItemAsync(Guid itemId, Guid coupleId, CancellationToken ct);

    Task<BankAccount?> FindAccountAsync(Guid id, Guid coupleId, CancellationToken ct);

    Task AddConnectionAsync(BankConnection connection, CancellationToken ct);

    Task AddItemAsync(BankItem item, CancellationToken ct);

    Task AddAccountAsync(BankAccount account, CancellationToken ct);

    /// <summary>
    /// Makes the next save fail with <c>ConcurrencyConflictException</c> (and write nothing at all) when the stored
    /// credentials of <paramref name="connection"/> are no longer the ones that were read, even if the save would
    /// otherwise not touch the connection.
    /// </summary>
    void RequireSameCredentialsOnSave(BankConnection connection);

    /// <summary>Discards the in-memory copy of <paramref name="connection"/> and reads it again, e.g. after a concurrency conflict.</summary>
    Task ReloadConnectionAsync(BankConnection connection, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
