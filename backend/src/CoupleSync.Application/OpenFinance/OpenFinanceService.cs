using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.OpenFinance;

/// <summary>
/// Open Finance through Meu Pluggy, phase 1: each person stores the credentials of their own Pluggy application
/// (encrypted) and the items (banks) they connected there; the accounts found are shown to the whole group.
/// Only who connected changes or disconnects. Nothing is synchronised here.
/// </summary>
public sealed class OpenFinanceService
{
    public const string UnavailableCode = "OPENFINANCE_UNAVAILABLE";
    public const string ItemEmptyCode = "PLUGGY_ITEM_EMPTY";

    public const string ItemEmptyMessage =
        "Nenhuma conta neste item. Confira se o conector MeuPluggy está ligado na aplicação e se a conexão foi feita pela Demo com a sua conta do Meu Pluggy.";

    private const string FormerMemberName = "Pessoa que saiu do grupo";

    private readonly IBankConnectionRepository _repository;
    private readonly ICoupleRepository _coupleRepository;
    private readonly IPluggyClient _pluggy;
    private readonly ICredentialCipher _cipher;
    private readonly IDateTimeProvider _clock;

    public OpenFinanceService(
        IBankConnectionRepository repository,
        ICoupleRepository coupleRepository,
        IPluggyClient pluggy,
        ICredentialCipher cipher,
        IDateTimeProvider clock)
    {
        _repository = repository;
        _coupleRepository = coupleRepository;
        _pluggy = pluggy;
        _cipher = cipher;
        _clock = clock;
    }

    /// <summary>Everything the group connected, and whether this server can connect at all.</summary>
    public async Task<OpenFinanceStatusDto> GetStatusAsync(Guid coupleId, Guid userId, CancellationToken ct)
    {
        var connections = await _repository.GetConnectionsAsync(coupleId, ct);
        if (connections.Count == 0)
            return new OpenFinanceStatusDto(_cipher.IsAvailable, []);

        var items = await _repository.GetItemsAsync(coupleId, ct);
        var accounts = await _repository.GetAccountsAsync(coupleId, ct);
        var names = await _coupleRepository.GetMemberNamesAsync(coupleId, ct);

        var accountsByItem = accounts.ToLookup(a => a.ItemId);
        var itemsByConnection = items.ToLookup(i => i.ConnectionId);

        var result = connections
            .OrderByDescending(c => c.UserId == userId)
            .ThenBy(c => c.CreatedAtUtc)
            .Select(c => MapConnection(
                c,
                names.TryGetValue(c.UserId, out var name) ? name : FormerMemberName,
                userId,
                itemsByConnection[c.Id].Select(i => MapItem(i, accountsByItem[i.Id])).ToList()))
            .ToList();

        return new OpenFinanceStatusDto(_cipher.IsAvailable, result);
    }

    /// <summary>Asks Pluggy whether the credentials work. Stores nothing.</summary>
    public async Task TestCredentialsAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        EnsureAvailable();
        await _pluggy.AuthenticateAsync(clientId.Trim(), clientSecret.Trim(), ct);
    }

    public async Task<BankConnectionDto> CreateConnectionAsync(Guid coupleId, Guid userId, CreateBankConnectionInput input, CancellationToken ct)
    {
        EnsureAvailable();

        // One connection per person and group. A disconnected one is the same connection, waiting for new credentials.
        var existing = await _repository.FindConnectionOfUserAsync(coupleId, userId, ct);
        if (existing is not null && existing.Status != BankConnectionStatus.Disconnected)
            throw AlreadyConnected();

        var clientId = input.ClientId.Trim();
        var clientSecret = input.ClientSecret.Trim();
        var historyMonths = input.HistoryMonths ?? BankConnection.DefaultHistoryMonths;

        // Credentials Pluggy refuses are never stored.
        await _pluggy.AuthenticateAsync(clientId, clientSecret, ct);

        var now = _clock.UtcNow;
        var encryptedId = _cipher.Encrypt(clientId);
        var encryptedSecret = _cipher.Encrypt(clientSecret);
        var hint = BankConnection.HintOf(clientId);

        BankConnection connection;
        if (existing is null)
        {
            connection = BankConnection.Create(coupleId, userId, input.Label, encryptedId, encryptedSecret, hint, historyMonths, now);
            await _repository.AddConnectionAsync(connection, ct);
        }
        else
        {
            connection = existing;
            connection.Connect(input.Label, encryptedId, encryptedSecret, hint, historyMonths, now);
            _pluggy.ForgetConnection(connection.Id);
        }

        try
        {
            await _repository.SaveChangesAsync(ct);
        }
        catch (UniqueViolationException)
        {
            // The same person, twice at the same time: the other request stored the connection first.
            throw AlreadyConnected();
        }

        var names = await _coupleRepository.GetMemberNamesAsync(coupleId, ct);
        var items = (await _repository.GetItemsAsync(coupleId, ct)).Where(i => i.ConnectionId == connection.Id).ToList();
        var accountsByItem = (await _repository.GetAccountsAsync(coupleId, ct)).ToLookup(a => a.ItemId);
        return MapConnection(
            connection,
            names.TryGetValue(userId, out var name) ? name : FormerMemberName,
            userId,
            items.Select(i => MapItem(i, accountsByItem[i.Id])).ToList());
    }

    /// <summary>Checks an item with the connection's credentials and stores it with the accounts Pluggy lists.</summary>
    public async Task<BankItemDto> AddItemAsync(Guid coupleId, Guid userId, Guid connectionId, string itemId, CancellationToken ct)
    {
        EnsureAvailable();
        var connection = await GetOwnConnectionAsync(connectionId, coupleId, userId, ct);

        if (!connection.HasCredentials)
            throw new ConflictException(
                "BANK_CONNECTION_DISCONNECTED",
                "Esta conexão foi desconectada. Conecte de novo com o Client ID e o Client Secret para adicionar bancos.");

        if (!_cipher.TryDecrypt(connection.ClientIdEncrypted!, out var clientId)
            || !_cipher.TryDecrypt(connection.ClientSecretEncrypted!, out var clientSecret))
        {
            // Stored with another key (the server's key changed): nothing can be read back.
            throw new UnprocessableEntityException(
                "OPENFINANCE_CREDENTIALS_UNREADABLE",
                "Não foi possível ler as credenciais guardadas. Desconecte e conecte de novo com o Client ID e o Client Secret.");
        }

        var auth = PluggyAuth.ForConnection(connection.Id, clientId, clientSecret);
        PluggyItem pluggyItem;
        IReadOnlyList<PluggyAccount> pluggyAccounts;
        try
        {
            pluggyItem = await _pluggy.GetItemAsync(auth, itemId.Trim(), ct);
            if (pluggyItem.Status is PluggyItemStatus.LoginError or PluggyItemStatus.WaitingUserInput)
                throw new PluggyException(PluggyErrorCodes.ItemNeedsAction);

            pluggyAccounts = await _pluggy.GetAccountsAsync(auth, pluggyItem.Id, ct);
        }
        catch (PluggyException ex) when (ex.Code == PluggyErrorCodes.InvalidCredentials)
        {
            // The stored credentials stopped working (regenerated or revoked at Pluggy): the group sees why.
            connection.MarkError(ex.Code, ex.Message, _clock.UtcNow);
            await _repository.SaveChangesAsync(ct);
            throw;
        }

        if (pluggyAccounts.Count == 0)
            throw new UnprocessableEntityException(ItemEmptyCode, ItemEmptyMessage);

        var now = _clock.UtcNow;
        var item = await _repository.FindItemByPluggyIdAsync(pluggyItem.Id, coupleId, ct);
        if (item is not null && item.ConnectionId != connection.Id)
            throw ItemAlreadyConnected();

        var storedAccounts = new List<BankAccount>();
        if (item is null)
        {
            item = BankItem.Create(
                coupleId, connection.Id, pluggyItem.Id, pluggyItem.ConnectorName, pluggyItem.Status,
                pluggyItem.ExecutionStatus, pluggyItem.UpdatedAtUtc, pluggyItem.ErrorMessage, now);
            await _repository.AddItemAsync(item, ct);
        }
        else
        {
            item.Refresh(pluggyItem.ConnectorName, pluggyItem.Status, pluggyItem.ExecutionStatus, pluggyItem.UpdatedAtUtc, pluggyItem.ErrorMessage);
            storedAccounts.AddRange(await _repository.GetAccountsOfItemAsync(item.Id, coupleId, ct));
        }

        var found = new List<BankAccount>();
        foreach (var pluggyAccount in pluggyAccounts.Where(a => !string.IsNullOrWhiteSpace(a.Id)).DistinctBy(a => a.Id.Trim()))
        {
            var snapshot = ToSnapshot(pluggyAccount);
            var account = storedAccounts.FirstOrDefault(a => a.PluggyAccountId == pluggyAccount.Id.Trim());
            if (account is null)
            {
                account = BankAccount.Create(coupleId, item.Id, pluggyAccount.Id, snapshot, now);
                await _repository.AddAccountAsync(account, ct);
            }
            else
            {
                account.Refresh(snapshot, now);
            }

            found.Add(account);
        }

        if (found.Count == 0)
            throw new UnprocessableEntityException(ItemEmptyCode, ItemEmptyMessage);

        connection.MarkWorking(now);

        try
        {
            await _repository.SaveChangesAsync(ct);
        }
        catch (UniqueViolationException)
        {
            // The item (or one of its accounts) is already stored under a connection this request cannot see:
            // another group's, or one written at this very moment.
            throw ItemAlreadyConnected();
        }

        return MapItem(item, found);
    }

    public async Task<BankAccountDto> SetAccountSyncAsync(Guid coupleId, Guid userId, Guid accountId, bool syncEnabled, CancellationToken ct)
    {
        EnsureAvailable();

        var account = await _repository.FindAccountAsync(accountId, coupleId, ct) ?? throw AccountNotFound();
        var item = await _repository.FindItemAsync(account.ItemId, coupleId, ct) ?? throw AccountNotFound();
        await GetOwnConnectionAsync(item.ConnectionId, coupleId, userId, ct);

        account.SetSyncEnabled(syncEnabled, _clock.UtcNow);
        await _repository.SaveChangesAsync(ct);
        return MapAccount(account);
    }

    /// <summary>Erases the credentials at once. Items and accounts stay, for the group's history.</summary>
    public async Task DisconnectAsync(Guid coupleId, Guid userId, Guid connectionId, CancellationToken ct)
    {
        EnsureAvailable();
        var connection = await GetOwnConnectionAsync(connectionId, coupleId, userId, ct);

        connection.Disconnect(_clock.UtcNow);
        _pluggy.ForgetConnection(connection.Id);
        await _repository.SaveChangesAsync(ct);
    }

    private void EnsureAvailable()
    {
        if (!_cipher.IsAvailable)
            throw new AppException(
                UnavailableCode,
                "O Open Finance não está disponível neste servidor. Ele depende de uma configuração que ainda não foi feita.",
                503);
    }

    /// <summary>The connection, when it is in the group (else 404) and belongs to the caller (else 403).</summary>
    private async Task<BankConnection> GetOwnConnectionAsync(Guid connectionId, Guid coupleId, Guid userId, CancellationToken ct)
    {
        var connection = await _repository.FindConnectionAsync(connectionId, coupleId, ct)
            ?? throw new NotFoundException("BANK_CONNECTION_NOT_FOUND", "Conexão bancária não encontrada.");

        if (connection.UserId != userId)
            throw new ForbiddenException(
                "BANK_CONNECTION_FORBIDDEN",
                "Só quem conectou pode alterar ou desconectar esta conexão bancária.");

        return connection;
    }

    private static NotFoundException AccountNotFound()
        => new("BANK_ACCOUNT_NOT_FOUND", "Conta bancária não encontrada.");

    private static ConflictException AlreadyConnected()
        => new("BANK_CONNECTION_ALREADY_EXISTS", "Você já tem uma conexão bancária neste grupo.");

    private static ConflictException ItemAlreadyConnected()
        => new("BANK_ITEM_ALREADY_CONNECTED", "Este Item ID já está conectado em outra conexão bancária.");

    private static BankAccountSnapshot ToSnapshot(PluggyAccount account) => new(
        account.Type,
        account.Subtype,
        account.Name,
        account.MarketingName,
        account.Number,
        account.CurrencyCode,
        account.Balance,
        account.CreditData?.CreditLimit,
        account.CreditData?.AvailableCreditLimit,
        account.CreditData?.BalanceCloseDate,
        account.CreditData?.BalanceDueDate,
        account.CreditData?.MinimumPayment,
        account.CreditData?.Brand);

    private static BankConnectionDto MapConnection(BankConnection c, string userName, Guid currentUserId, IReadOnlyList<BankItemDto> items) => new(
        c.Id,
        c.Label,
        c.UserId,
        userName,
        c.UserId == currentUserId,
        c.Status.ToString(),
        c.ClientIdHint,
        c.HistoryMonths,
        c.LastSyncAtUtc,
        c.LastErrorCode,
        c.LastErrorMessage,
        c.CreatedAtUtc,
        items);

    private static BankItemDto MapItem(BankItem item, IEnumerable<BankAccount> accounts) => new(
        item.Id,
        item.ConnectorName,
        item.Status,
        item.ExecutionStatus,
        item.LastUpdatedAtUtc,
        item.LastErrorMessage,
        accounts
            .OrderBy(a => a.Type, StringComparer.Ordinal)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Id)
            .Select(MapAccount)
            .ToList());

    private static BankAccountDto MapAccount(BankAccount a) => new(
        a.Id,
        a.Type,
        a.Subtype,
        a.Name,
        a.MarketingName,
        a.NumberMasked,
        a.Currency,
        a.Balance,
        a.BalanceAtUtc,
        a.CreditLimit,
        a.AvailableCreditLimit,
        a.BalanceCloseDate,
        a.BalanceDueDate,
        a.MinimumPayment,
        a.Brand,
        a.SyncEnabled);
}
