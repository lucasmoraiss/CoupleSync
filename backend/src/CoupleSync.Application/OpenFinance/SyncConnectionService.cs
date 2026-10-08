using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Application.OpenFinance;

/// <summary>
/// Executes one <see cref="SyncRun"/>: reads items, accounts and transactions of a connection at Pluggy and writes
/// them to the mirror (<c>bank_transactions</c>). Nothing becomes a transaction of the app here: that is the review.
/// Every write checks that the credentials of the connection are still the ones the run read; when the person
/// disconnected, connected again or left the group meanwhile, the run stops and never writes the connection back.
/// </summary>
public sealed class SyncConnectionService
{
    public const string FailedCode = "SYNC_FAILED";
    public const string InterruptedCode = "SYNC_INTERRUPTED";
    public const string OwnerNotInGroupCode = "SYNC_OWNER_NOT_IN_GROUP";
    public const string NoBankCode = "SYNC_NO_BANK";

    public const string InterruptedMessage =
        "A sincronização foi interrompida porque o servidor reiniciou. Sincronize de novo.";
    public const string FailedMessage = "Não foi possível sincronizar agora. Tente de novo em alguns minutos.";
    public const string OwnerNotInGroupMessage =
        "Quem conectou estes bancos não faz mais parte do grupo. Nada foi sincronizado.";
    public const string ChangedMessage =
        "A conexão foi desconectada ou alterada durante a sincronização. Nada mais foi gravado.";
    public const string DisconnectedMessage =
        "Esta conexão foi desconectada. Conecte de novo com o Client ID e o Client Secret para sincronizar.";
    public const string NoBankMessage =
        "Nenhum banco verificado nesta conexão. Adicione um banco e sincronize de novo.";
    public const string NoBankReadableMessage =
        "Nenhum banco pôde ser lido: cada um precisa de uma ação sua no Meu Pluggy (entrar de novo ou autorizar). Resolva em meu.pluggy.ai e sincronize de novo.";

    /// <summary>How far back every run after the first reads again: banks post and correct transactions late.</summary>
    public const int OverlapDays = 7;

    /// <summary>Descriptions sent to the AI classifier in one run, at most (the rest stays with the table).</summary>
    public const int MaxAiSuggestionsPerRun = 30;

    private const int LookupChunk = 400;

    private static readonly TimeSpan AiTimeBudget = TimeSpan.FromSeconds(15);

    private static readonly IReadOnlyList<string> CategoryLabels = TransactionCategories.All.Select(c => c.Label).ToList();

    private readonly IBankSyncRepository _sync;
    private readonly IBankConnectionRepository _connections;
    private readonly ICoupleMembership _membership;
    private readonly IPluggyClient _pluggy;
    private readonly ICredentialCipher _cipher;
    private readonly ICategoryClassifier _classifier;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<SyncConnectionService> _logger;

    public SyncConnectionService(
        IBankSyncRepository sync,
        IBankConnectionRepository connections,
        ICoupleMembership membership,
        IPluggyClient pluggy,
        ICredentialCipher cipher,
        ICategoryClassifier classifier,
        IDateTimeProvider clock,
        ILogger<SyncConnectionService> logger)
    {
        _sync = sync;
        _connections = connections;
        _membership = membership;
        _pluggy = pluggy;
        _cipher = cipher;
        _classifier = classifier;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Fails every run left in <c>Running</c>: the process that was executing it is gone (the API sleeps and restarts
    /// on the free tier), so nothing would ever finish it.
    /// </summary>
    public async Task<int> RecoverStuckRunsAsync(CancellationToken ct)
    {
        var stuck = await _sync.GetRunningRunsForJobAsync(ct);
        if (stuck.Count == 0) return 0;

        var now = _clock.UtcNow;
        foreach (var run in stuck)
            run.MarkFailed(InterruptedCode, InterruptedMessage, now);

        try
        {
            await _sync.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // A run was deleted at this very moment (the person left the group): the next start finds the rest.
            _sync.DiscardChanges();
            return 0;
        }

        return stuck.Count;
    }

    /// <summary>The runs waiting, oldest first. The job executes them one after the other, each in its own scope.</summary>
    public Task<IReadOnlyList<Guid>> GetPendingRunIdsAsync(int max, CancellationToken ct)
        => _sync.GetPendingRunIdsForJobAsync(max, ct);

    /// <summary>Executes the run when it is still waiting. Never throws for a failure of the run itself: the run records it.</summary>
    public async Task ExecuteAsync(Guid runId, CancellationToken ct)
    {
        var run = await _sync.FindRunForJobAsync(runId, ct);
        if (run is null || run.Status != SyncRunStatus.Pending) return;

        run.MarkRunning(_clock.UtcNow);
        try
        {
            await _sync.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            _sync.DiscardChanges(); // taken by another worker, or deleted meanwhile (the person left the group)
            return;
        }

        try
        {
            await RunAsync(run, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the server is stopping: the next start fails this run as interrupted
        }
        catch (SyncStoppedException stop)
        {
            await FailAsync(run.Id, stop.Code, stop.Message, markConnection: false, ct);
        }
        catch (PluggyException ex)
        {
            var refused = ex.Code == PluggyErrorCodes.InvalidCredentials;
            await FailAsync(
                run.Id,
                ex.Code,
                refused ? OpenFinanceService.StoredCredentialsRefusedMessage : ex.Message,
                markConnection: refused,
                ct);
        }
        catch (Exception ex)
        {
            // Only the kind of failure: a message of the data layer could carry what was being written.
            _logger.LogError("Open Finance run {RunId} failed with {ExceptionType}.", run.Id, ex.GetType().Name);
            await FailAsync(run.Id, FailedCode, FailedMessage, markConnection: false, ct);
        }
    }

    private async Task RunAsync(SyncRun run, CancellationToken ct)
    {
        var connection = await _connections.FindConnectionAsync(run.ConnectionId, run.CoupleId, ct)
            ?? throw new SyncStoppedException(OpenFinanceService.ChangedCode, ChangedMessage);

        if (connection.Status == BankConnectionStatus.Disconnected || !connection.HasCredentials)
            throw new SyncStoppedException(OpenFinanceService.DisconnectedCode, DisconnectedMessage);

        // A connection whose person is no longer in the group is nobody's: nothing of it is read (Pluggy is not called).
        if (!await _membership.IsMemberAsync(connection.UserId, connection.CoupleId, ct))
            throw new SyncStoppedException(OwnerNotInGroupCode, OwnerNotInGroupMessage);

        if (!_cipher.IsAvailable)
            throw new SyncStoppedException(OpenFinanceService.UnavailableCode, OpenFinanceService.UnavailableMessage);

        if (!_cipher.TryDecrypt(connection.ClientIdEncrypted!, out var clientId)
            || !_cipher.TryDecrypt(connection.ClientSecretEncrypted!, out var clientSecret))
        {
            throw new SyncStoppedException(OpenFinanceService.CredentialsUnreadableCode, OpenFinanceService.CredentialsUnreadableMessage);
        }

        var items = await _sync.GetItemsOfConnectionAsync(connection.Id, connection.CoupleId, ct);
        if (items.Count == 0)
            throw new SyncStoppedException(NoBankCode, NoBankMessage);

        var auth = PluggyAuth.ForConnection(connection.Id, clientId, clientSecret, connection.ClientSecretEncrypted!);
        var window = SyncWindow.For(connection, _clock.UtcNow);
        var ai = new AiBudget(run.AiCategorizationConsent ? MaxAiSuggestionsPerRun : 0, _clock.UtcNow + AiTimeBudget);

        var itemsRead = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (await SyncItemAsync(run, connection, auth, item, window, ai, ct)) itemsRead++;
        }

        if (itemsRead == 0)
            throw new SyncStoppedException(PluggyErrorCodes.ItemNeedsAction, NoBankReadableMessage);

        // Read again right before the verdict: a connection disconnected meanwhile is never written back.
        if (!await _sync.ConnectionStillHasCredentialsAsync(connection.Id, connection.ClientSecretEncrypted!, ct))
        {
            _pluggy.ForgetConnection(connection.Id);
            throw new SyncStoppedException(OpenFinanceService.ChangedCode, ChangedMessage);
        }

        var now = _clock.UtcNow;
        connection.MarkSynced(now);
        run.MarkDone(now);
        await SaveAsync(connection, ct);
    }

    /// <summary>True when the item was read; false when it needs the person at the bank (the run goes on with the others).</summary>
    private async Task<bool> SyncItemAsync(
        SyncRun run, BankConnection connection, PluggyAuth auth, BankItem item, SyncWindow window, AiBudget ai, CancellationToken ct)
    {
        try
        {
            if (run.ForceItemUpdate)
                await RequestUpdateAsync(auth, item, ct);

            var pluggyItem = await _pluggy.GetItemAsync(auth, item.PluggyItemId, ct);
            item.Refresh(pluggyItem.ConnectorName, pluggyItem.Status, pluggyItem.ExecutionStatus, pluggyItem.UpdatedAtUtc, pluggyItem.ErrorMessage);
            if (pluggyItem.Status is PluggyItemStatus.LoginError or PluggyItemStatus.WaitingUserInput)
            {
                await SaveAsync(connection, ct);
                return false;
            }

            var accounts = await UpsertAccountsAsync(connection, item, auth, ct);
            await SaveAsync(connection, ct);

            foreach (var account in accounts.Where(a => a.SyncEnabled))
            {
                ct.ThrowIfCancellationRequested();
                // An account never read before gets the whole history the person chose, also when it was added later.
                var from = await _sync.AccountHasTransactionsAsync(account.Id, ct) ? window.From : window.HistoryFrom;
                var transactions = await _pluggy.GetTransactionsAsync(auth, account.PluggyAccountId, from, window.To, ct);
                await UpsertTransactionsAsync(run, connection, account, transactions, ai, ct);
                await SaveAsync(connection, ct);
            }

            return true;
        }
        catch (PluggyException ex) when (ex.Code is PluggyErrorCodes.ItemNotFound or PluggyErrorCodes.ItemNeedsAction)
        {
            item.Refresh(item.ConnectorName, item.Status, item.ExecutionStatus, item.LastUpdatedAtUtc, ex.Message);
            await SaveAsync(connection, ct);
            return false;
        }
    }

    private async Task RequestUpdateAsync(PluggyAuth auth, BankItem item, CancellationToken ct)
    {
        try
        {
            await _pluggy.RequestItemUpdateAsync(auth, item.PluggyItemId, ct);
        }
        catch (PluggyException ex) when (ex.Code != PluggyErrorCodes.InvalidCredentials)
        {
            // Pluggy would not start a new reading now (limit, item busy): what it already has is read below.
        }
    }

    private async Task<IReadOnlyList<BankAccount>> UpsertAccountsAsync(BankConnection connection, BankItem item, PluggyAuth auth, CancellationToken ct)
    {
        var pluggyAccounts = await _pluggy.GetAccountsAsync(auth, item.PluggyItemId, ct);
        var stored = (await _connections.GetAccountsOfItemAsync(item.Id, connection.CoupleId, ct)).ToList();
        var now = _clock.UtcNow;

        foreach (var pluggyAccount in pluggyAccounts.Where(a => !string.IsNullOrWhiteSpace(a.Id)).DistinctBy(a => a.Id.Trim()))
        {
            var snapshot = OpenFinanceService.ToSnapshot(pluggyAccount);
            var account = stored.FirstOrDefault(a => a.PluggyAccountId == pluggyAccount.Id.Trim());
            if (account is null)
            {
                account = BankAccount.Create(connection.CoupleId, item.Id, pluggyAccount.Id, snapshot, now);
                await _connections.AddAccountAsync(account, ct);
                stored.Add(account);
            }
            else
            {
                account.Refresh(snapshot, now);
            }
        }

        return stored;
    }

    private async Task UpsertTransactionsAsync(
        SyncRun run, BankConnection connection, BankAccount account, IReadOnlyList<PluggyTransaction> transactions, AiBudget ai, CancellationToken ct)
    {
        var incoming = transactions
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && t.Id.Trim().Length <= BankTransaction.MaxPluggyIdLength)
            .DistinctBy(t => t.Id.Trim())
            .ToList();
        var now = _clock.UtcNow;
        var added = 0;
        var updated = 0;

        foreach (var chunk in incoming.Chunk(LookupChunk))
        {
            var existing = (await _sync.FindByPluggyIdsAsync(chunk.Select(t => t.Id.Trim()).ToList(), ct))
                .ToDictionary(t => t.PluggyTransactionId, StringComparer.Ordinal);
            var created = new List<BankTransaction>();

            foreach (var transaction in chunk)
            {
                var snapshot = ToSnapshot(transaction);
                if (existing.TryGetValue(transaction.Id.Trim(), out var row))
                {
                    // The id is unique in the whole database: a row of another account is not this account's to change.
                    if (row.BankAccountId != account.Id) continue;
                    row.Refresh(snapshot, run.Id, now);
                    updated++;
                    continue;
                }

                var suggested = BankTransaction.TypeOf(transaction.Type, transaction.Amount) == BankTransactionType.Debit
                    ? await SuggestCategoryAsync(transaction, ai, ct)
                    : null;
                created.Add(BankTransaction.Create(
                    connection.CoupleId, connection.UserId, account.Id, transaction.Id, snapshot, suggested, run.Id, now));
                added++;
            }

            if (created.Count > 0) await _sync.AddTransactionsAsync(created, ct);
        }

        run.AddCounts(added, updated);
    }

    /// <summary>
    /// The table first. Only what it does not know, with the consent of who connected and a description to show,
    /// goes to the AI classifier, within a budget of calls and of time. Anything else is OUTROS.
    /// </summary>
    private async Task<string> SuggestCategoryAsync(PluggyTransaction transaction, AiBudget ai, CancellationToken ct)
    {
        var mapped = PluggyCategoryMap.TryMap(transaction.CategoryId, transaction.Category);
        if (mapped is not null) return mapped;

        var description = transaction.Description ?? transaction.DescriptionRaw;
        if (string.IsNullOrWhiteSpace(description) || !ai.TryTake(_clock.UtcNow)) return TransactionCategories.Other;

        try
        {
            var suggested = await _classifier.SuggestCategoryAsync(description.Trim(), CategoryLabels, ct);
            return TransactionCategories.TryNormalize(suggested) ?? TransactionCategories.Other;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ai.Stop(); // the classifier is optional: a failure of it never fails the synchronisation
            return TransactionCategories.Other;
        }
    }

    private async Task SaveAsync(BankConnection connection, CancellationToken ct)
    {
        try
        {
            await _sync.SaveRunProgressAsync(connection, ct);
        }
        catch (DataStoreException ex) when (ex is ConcurrencyConflictException or ForeignKeyViolationException)
        {
            // Disconnected, connected again, or gone with the person who left the group. The API key this run got
            // belongs to credentials that are no longer the stored ones: it is not kept.
            _pluggy.ForgetConnection(connection.Id);
            throw new SyncStoppedException(OpenFinanceService.ChangedCode, ChangedMessage);
        }
    }

    /// <summary>
    /// Records the failure and nothing else: whatever the run had not saved is dropped. The connection is marked
    /// only when Pluggy refused its credentials, and only if they are still the ones stored.
    /// </summary>
    private async Task FailAsync(Guid runId, string code, string message, bool markConnection, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            _sync.DiscardChanges();
            var run = await _sync.FindRunForJobAsync(runId, ct);
            // Deleted with the connection, or already given a verdict by someone else (the recovery of another process).
            if (run is null || run.Status != SyncRunStatus.Running) return;

            var now = _clock.UtcNow;
            run.MarkFailed(code, message, now);

            BankConnection? connection = null;
            if (markConnection && attempt == 1)
            {
                connection = await _connections.FindConnectionAsync(run.ConnectionId, run.CoupleId, ct);
                if (connection is { HasCredentials: true })
                    connection.MarkError(code, message, now);
                else
                    connection = null;
            }

            try
            {
                if (connection is not null)
                    await _sync.SaveRunProgressAsync(connection, ct);
                else
                    await _sync.SaveChangesAsync(ct);
                return;
            }
            catch (DataStoreException ex) when (ex is ConcurrencyConflictException or ForeignKeyViolationException)
            {
                // The connection (or the run) changed while this was being written: read again, and only the run is recorded.
            }
        }

        _sync.DiscardChanges();
    }

    private static BankTransactionSnapshot ToSnapshot(PluggyTransaction t) => new(
        t.DateUtc,
        t.Amount,
        t.Type,
        t.CurrencyCode,
        t.Description,
        t.DescriptionRaw,
        t.Category,
        t.CategoryId,
        t.MerchantName,
        t.MerchantCnpj,
        t.MerchantCategory,
        t.PaymentMethod,
        t.InstallmentNumber,
        t.TotalInstallments,
        t.BillId,
        t.Status,
        t.Balance,
        t.RawJson);

    /// <summary>The run cannot go on, for a reason the person can read.</summary>
    private sealed class SyncStoppedException : Exception
    {
        public SyncStoppedException(string code, string message) : base(message) => Code = code;

        public string Code { get; }
    }

    private sealed class AiBudget
    {
        private int _calls;
        private readonly DateTime _deadlineUtc;

        public AiBudget(int calls, DateTime deadlineUtc)
        {
            _calls = calls;
            _deadlineUtc = deadlineUtc;
        }

        public bool TryTake(DateTime nowUtc)
        {
            if (_calls <= 0 || nowUtc > _deadlineUtc) return false;
            _calls--;
            return true;
        }

        public void Stop() => _calls = 0;
    }
}

/// <summary>
/// The days a run asks Pluggy for. The first time: today (in Brazil) minus the months the person chose. After that:
/// the day (in Brazil) of the last successful synchronisation minus <see cref="SyncConnectionService.OverlapDays"/>.
/// The last day is today in UTC, which is never behind Brazil: a purchase of this evening is already inside.
/// </summary>
public sealed record SyncWindow(DateOnly From, DateOnly HistoryFrom, DateOnly To)
{
    public static SyncWindow For(BankConnection connection, DateTime nowUtc)
    {
        var today = DateOnly.FromDateTime(BrazilTime.ToLocal(nowUtc));
        var historyFrom = today.AddMonths(-connection.HistoryMonths);
        var from = connection.LastSyncAtUtc is { } last
            ? DateOnly.FromDateTime(BrazilTime.ToLocal(last)).AddDays(-SyncConnectionService.OverlapDays)
            : historyFrom;
        return new SyncWindow(from, historyFrom, DateOnly.FromDateTime(nowUtc));
    }
}
