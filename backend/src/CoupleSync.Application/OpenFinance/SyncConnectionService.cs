using System.Diagnostics;
using System.Globalization;
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
/// Every run leaves one line in the log with how it ended (and a warning when it failed): only its id, codes, counts
/// and times, never anything of a bank, of an account or of a transaction.
/// </summary>
public sealed class SyncConnectionService
{
    public const string FailedCode = "SYNC_FAILED";
    public const string InterruptedCode = "SYNC_INTERRUPTED";
    public const string OwnerNotInGroupCode = "SYNC_OWNER_NOT_IN_GROUP";
    public const string NoBankCode = "SYNC_NO_BANK";
    public const string TimedOutCode = "SYNC_TIMED_OUT";

    /// <summary>Pluggy did not hand over the whole listing of some account: nothing of that listing is stored, the run is not a success.</summary>
    public const string IncompleteCode = "SYNC_INCOMPLETE";

    public const string InterruptedMessage =
        "A sincronização foi interrompida porque o servidor reiniciou. Sincronize de novo.";
    public const string TimedOutMessage =
        "A sincronização demorou demais e foi encerrada. Sincronize de novo.";
    public const string FailedMessage = "Não foi possível sincronizar agora. Tente de novo em alguns minutos.";
    public const string IncompleteMessage =
        "O Pluggy não entregou a lista completa das transações desta vez, e nada dessa lista foi guardado. Tente sincronizar de novo.";
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

    /// <summary>
    /// A run still <c>Running</c> this long after it started is nobody's any more (its process died, or could not
    /// record the verdict): every pass of the job fails it, so the connection is never blocked by it for longer.
    /// </summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long the verdict "interrupted" may take to be written while the server is stopping.</summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private const int LookupChunk = 400;

    /// <summary>The time one run may spend waiting for the AI classifier: only its answers count, nothing else of the run.</summary>
    private static readonly TimeSpan AiTimeBudget = TimeSpan.FromSeconds(15);

    private static readonly IReadOnlyList<string> CategoryLabels = TransactionCategories.All.Select(c => c.Label).ToList();

    private readonly IBankSyncRepository _sync;
    private readonly IBankConnectionRepository _connections;
    private readonly ICoupleMembership _membership;
    private readonly IPluggyClient _pluggy;
    private readonly ICredentialCipher _cipher;
    private readonly ICategoryClassifier _classifier;
    private readonly BankPreclassificationService _preclassification;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<SyncConnectionService> _logger;

    public SyncConnectionService(
        IBankSyncRepository sync,
        IBankConnectionRepository connections,
        ICoupleMembership membership,
        IPluggyClient pluggy,
        ICredentialCipher cipher,
        ICategoryClassifier classifier,
        BankPreclassificationService preclassification,
        IDateTimeProvider clock,
        ILogger<SyncConnectionService> logger)
    {
        _sync = sync;
        _connections = connections;
        _membership = membership;
        _pluggy = pluggy;
        _cipher = cipher;
        _classifier = classifier;
        _preclassification = preclassification;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// At start: fails every run left in <c>Running</c>. The process that was executing it is gone (the API sleeps
    /// and restarts on the free tier), so nothing would ever finish it.
    /// </summary>
    public Task<int> RecoverStuckRunsAsync(CancellationToken ct)
        => FailRunningRunsAsync(startedBeforeUtc: null, InterruptedCode, InterruptedMessage, ct);

    /// <summary>
    /// At every pass of the job: fails the runs <c>Running</c> for longer than <see cref="RunTimeout"/>. The start
    /// alone is not enough: in a deploy the new process starts (and recovers) before the old one is told to stop,
    /// and a verdict may fail to be written when the database is unreachable for a moment.
    /// </summary>
    public Task<int> RecoverTimedOutRunsAsync(CancellationToken ct)
        => FailRunningRunsAsync(_clock.UtcNow - RunTimeout, TimedOutCode, TimedOutMessage, ct);

    private async Task<int> FailRunningRunsAsync(DateTime? startedBeforeUtc, string code, string message, CancellationToken ct)
    {
        var stuck = await _sync.GetRunningRunsForJobAsync(startedBeforeUtc, ct);
        if (stuck.Count == 0) return 0;

        var now = _clock.UtcNow;
        foreach (var run in stuck)
            run.MarkFailed(code, message, now);

        try
        {
            await _sync.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // A run got its verdict or was deleted at this very moment (the person left the group): the next pass
            // finds the rest.
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

        var progress = new RunProgress();
        var watch = Stopwatch.StartNew();
        try
        {
            await RunAsync(run, progress, ct);
            LogEnd(run.Id, "Done", progress, watch);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            LogFailure(run.Id, InterruptedCode, progress, watch, pluggy: null);
            // The server is stopping. Another process may already be up (a deploy) and will not look at this run at
            // its start any more: the verdict is written now, in a few seconds of grace. If even that fails, the
            // pass of any process fails the run after RunTimeout.
            try
            {
                using var grace = new CancellationTokenSource(ShutdownGrace);
                await FailAsync(run.Id, InterruptedCode, InterruptedMessage, markConnection: false, grace.Token);
            }
            catch (Exception)
            {
                // Nothing else to do while stopping.
            }

            throw;
        }
        catch (SyncStoppedException stop)
        {
            LogFailure(run.Id, stop.Code, progress, watch, pluggy: null);
            await FailAsync(run.Id, stop.Code, stop.Message, markConnection: false, ct, stop.WhereItStopped);
        }
        catch (PluggyException ex)
        {
            LogFailure(run.Id, ex.Code, progress, watch, ex);
            var refused = ex.Code == PluggyErrorCodes.InvalidCredentials;
            await FailAsync(
                run.Id,
                ex.Code,
                refused ? OpenFinanceService.StoredCredentialsRefusedMessage : ex.Message,
                markConnection: refused,
                ct,
                whereItStopped: WhereItStopped(ex));
        }
        catch (Exception ex)
        {
            // Only the kind of failure: a message of the data layer could carry what was being written.
            _logger.LogError("Open Finance run {RunId} failed with {ExceptionType}.", run.Id, ex.GetType().Name);
            LogFailure(run.Id, FailedCode, progress, watch, pluggy: null);
            await FailAsync(run.Id, FailedCode, FailedMessage, markConnection: false, ct);
        }
    }

    /// <summary>One line per run, however it ended: what production has to tell what a synchronisation did.</summary>
    private void LogEnd(Guid runId, string result, RunProgress progress, Stopwatch watch, string? stage = null)
        => _logger.LogInformation(
            "Open Finance run {RunId} ended: {Result}; stage {Stage}; accounts read {AccountsRead}; transactions new {TransactionsNew}, updated {TransactionsUpdated}; incomplete listings {IncompleteListings}; took {ElapsedMs} ms.",
            runId,
            result,
            stage ?? progress.Stage,
            progress.AccountsRead,
            progress.TransactionsNew,
            progress.TransactionsUpdated,
            progress.IncompleteListings,
            watch.ElapsedMilliseconds);

    /// <summary>
    /// A run that fails: a warning with the stage it stopped at and, when a call to Pluggy is the reason, which call,
    /// the status and the codes of Pluggy's error body. Then the line every run has.
    /// </summary>
    private void LogFailure(Guid runId, string code, RunProgress progress, Stopwatch watch, PluggyException? pluggy)
    {
        var stage = pluggy?.CallKind ?? progress.Stage;
        _logger.LogWarning(
            "Open Finance run {RunId} failed at stage {Stage} with {ErrorCode}; Pluggy call {Kind} answered {StatusCode} ({PluggyCode}).",
            runId,
            stage,
            code,
            pluggy?.CallKind ?? "none",
            pluggy is null ? "-" : pluggy.HttpStatus is { } status ? status.ToString(CultureInfo.InvariantCulture) : "no answer",
            pluggy?.PluggyCode ?? "-");
        LogEnd(runId, $"Failed ({code})", progress, watch, stage);
    }

    /// <summary>
    /// What goes after the message of a run that a call to Pluggy failed, for the diary of the run (the app shows
    /// it): the stage, the HTTP status and the codes of Pluggy's error body. Nothing else of the answer.
    /// </summary>
    private static string WhereItStopped(PluggyException ex)
    {
        if (ex.CallKind is not { } kind) return string.Empty;
        var stage = kind switch
        {
            "auth" => "autenticação",
            "item" => "banco",
            "item-update" => "atualização do banco",
            "accounts" => "contas",
            "transactions" => "transações",
            "bills" => "faturas",
            _ => kind,
        };
        if (ex.HttpStatus is not { } status) return $" (Etapa: {stage}; sem resposta válida do Pluggy)";

        // Pluggy's "code" is usually the HTTP status again ("410/ENDPOINT_DEPRECATED"): a person reads the number once.
        var http = status.ToString(CultureInfo.InvariantCulture);
        var pluggyCode = ex.PluggyCode;
        if (pluggyCode == http) pluggyCode = null;
        else if (pluggyCode is not null && pluggyCode.StartsWith(http + "/", StringComparison.Ordinal)) pluggyCode = pluggyCode[(http.Length + 1)..];
        var codes = pluggyCode is null ? string.Empty : $", {pluggyCode}";
        return $" (Etapa: {stage}; resposta do Pluggy: HTTP {http}{codes})";
    }

    private async Task RunAsync(SyncRun run, RunProgress progress, CancellationToken ct)
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

        // Before anything is asked of the bank: every line of the group that waits, in every account, is judged as a
        // suspected duplicate against what the app has now. The accounts this run will not read (synchronisation
        // turned off, a bank asking for the login again, another connection of the group, also a disconnected one)
        // are judged only here; and a run that fails further on has judged all the same.
        Task<bool> JudgeGroupAsync(CancellationToken token) => _preclassification.JudgeWaitingOfGroupAsync(connection.CoupleId, token);
        if (await JudgeGroupAsync(ct)) await SaveAsync(connection, ct, JudgeGroupAsync);

        var auth = PluggyAuth.ForConnection(connection.Id, clientId, clientSecret, connection.ClientSecretEncrypted!);
        var window = SyncWindow.For(connection, _clock.UtcNow);
        var ai = new AiBudget(run.AiCategorizationConsent ? MaxAiSuggestionsPerRun : 0, AiTimeBudget);

        var itemsRead = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (await SyncItemAsync(run, connection, auth, item, window, ai, progress, ct)) itemsRead++;
        }

        if (itemsRead == 0)
        {
            progress.Stage = RunProgress.Finish;
            throw new SyncStoppedException(PluggyErrorCodes.ItemNeedsAction, NoBankReadableMessage);
        }

        // Some listing stopped before Pluggy said it ended, and nothing of it was stored. The accounts whose listing
        // came whole are stored as always; but this is not a synchronisation that ended well: the run fails saying
        // so, and the connection keeps the date of its last whole synchronisation. The account left out is as it was
        // before this run, so the next run asks for the same days of it.
        if (progress.IncompleteListings > 0)
        {
            progress.Stage = "transactions";
            throw new SyncStoppedException(IncompleteCode, IncompleteMessage, " (Etapa: transações; listagem incompleta)");
        }

        progress.Stage = RunProgress.Finish;

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
        SyncRun run, BankConnection connection, PluggyAuth auth, BankItem item, SyncWindow window, AiBudget ai, RunProgress progress, CancellationToken ct)
    {
        try
        {
            progress.Stage = "item";
            if (run.ForceItemUpdate)
                await RequestUpdateAsync(auth, item, ct);

            var pluggyItem = await _pluggy.GetItemAsync(auth, item.PluggyItemId, ct);
            item.Refresh(pluggyItem.ConnectorName, pluggyItem.Status, pluggyItem.ExecutionStatus, pluggyItem.UpdatedAtUtc, pluggyItem.ErrorMessage);
            if (pluggyItem.Status is PluggyItemStatus.LoginError or PluggyItemStatus.WaitingUserInput)
            {
                await SaveAsync(connection, ct);
                return false;
            }

            progress.Stage = "accounts";
            var accounts = await UpsertAccountsAsync(connection, item, auth, ct);
            await SaveAsync(connection, ct);

            foreach (var account in accounts.Where(a => a.SyncEnabled))
            {
                ct.ThrowIfCancellationRequested();
                progress.Stage = "transactions";
                // By account, not by connection: the connection may have synchronised many times while this bank
                // could not be read (login error at the bank, another bank of the same connection going on alone).
                var from = window.FromFor(await _sync.GetLastTransactionDayAsync(account.Id, ct));
                var listing = await _pluggy.GetTransactionsAsync(auth, account.PluggyAccountId, from, window.To, ct);
                // A listing that stopped before Pluggy said it ended (a cursor that came back, a next page that could
                // not be read, the ceiling of pages) is left out whole: nothing of it is stored and nothing is removed
                // for it. Storing a part would move the last day the mirror has of the account, which is where the
                // next reading starts from, and the days that did not come would never be asked for again. The other
                // accounts are read all the same, and the run fails at its end (see RunAsync).
                if (!listing.Complete)
                {
                    progress.IncompleteListings++;
                    _logger.LogWarning(
                        "Open Finance run {RunId}: the transactions of an account did not come whole from Pluggy; nothing of that listing is stored or removed, and the run will end as incomplete.",
                        run.Id);
                    continue;
                }

                progress.AccountsRead++;
                var transactions = listing.Transactions;
                var (arrived, readAgain) = await UpsertTransactionsAsync(run, connection, account, transactions, ai, progress, ct);
                // What the bank no longer lists leaves in the same save as the line that replaces it.
                var leaving = await RemoveVanishedAsync(account, transactions, from, window.To, ct);
                // Before anyone sees them: transfers between accounts of the group, bill payments and suspected
                // duplicates. The last one also for every line of the account that already waited, of any age: what
                // the app has may have changed since they arrived, and the history stored before these rules existed
                // was never judged.
                Task DecideAsync(CancellationToken token)
                    => _preclassification.ApplyAsync(connection.CoupleId, account, item.ConnectorName, arrived, readAgain, leaving, token);
                await DecideAsync(ct);
                await SaveAsync(connection, ct, DecideAsync);

                if (BankReconciliationRules.IsCreditCard(account.Type))
                {
                    progress.Stage = "bills";
                    await SyncBillsAsync(connection, auth, account, ct);
                }
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
        catch (PluggyException)
        {
            // Pluggy would not start a new reading now (limit, item busy, or it does not allow this for the item at
            // all): what it already has is read below. No refusal of this request says anything about the stored
            // credentials: if they are bad, the reading right after says so.
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

    /// <summary>
    /// The bills of a credit card: mirrored by their Pluggy id (synchronising again never duplicates; total, due date
    /// and the rest take what Pluggy says now; a bill Pluggy no longer lists stays). Bills are an extra of the
    /// synchronisation: when Pluggy does not give them (whatever the reason, also a refusal that for any other
    /// reading would mean bad credentials), the run goes on and says nothing; if the credentials are really bad, the
    /// next reading of the run says so. When a bill arrived or changed, the debits already in the mirror are looked
    /// at again for its payment.
    /// </summary>
    private async Task SyncBillsAsync(BankConnection connection, PluggyAuth auth, BankAccount account, CancellationToken ct)
    {
        IReadOnlyList<PluggyBill> bills;
        try
        {
            bills = await _pluggy.GetBillsAsync(auth, account.PluggyAccountId, ct);
        }
        catch (PluggyException ex)
        {
            _logger.LogWarning("Open Finance bills of an account could not be read ({Code}); the synchronisation goes on without them.", ex.Code);
            return;
        }

        var changedDueDates = await UpsertBillsAsync(connection, account, bills, ct);
        await SaveAsync(connection, ct);
        if (changedDueDates.Count == 0) return;

        Task DecideAsync(CancellationToken token) => _preclassification.ApplyBillPaymentsAsync(connection.CoupleId, changedDueDates, token);
        await DecideAsync(ct);
        await SaveAsync(connection, ct, DecideAsync);
    }

    /// <summary>Returns the due dates of the bills that arrived or changed in what the payment of a bill is recognised by.</summary>
    private async Task<IReadOnlyCollection<DateOnly>> UpsertBillsAsync(
        BankConnection connection, BankAccount account, IReadOnlyList<PluggyBill> bills, CancellationToken ct)
    {
        var incoming = bills
            .Where(b => !string.IsNullOrWhiteSpace(b.Id) && b.Id.Trim().Length <= CreditCardBill.MaxPluggyIdLength)
            .DistinctBy(b => b.Id.Trim())
            .ToList();
        var now = _clock.UtcNow;
        var today = DateOnly.FromDateTime(BrazilTime.ToLocal(now));
        var changed = new HashSet<DateOnly>();

        foreach (var chunk in incoming.Chunk(LookupChunk))
        {
            var existing = (await _sync.FindBillsByPluggyIdsAsync(chunk.Select(b => b.Id.Trim()).ToList(), ct))
                .ToDictionary(b => b.PluggyBillId, StringComparer.Ordinal);
            var created = new List<CreditCardBill>();

            foreach (var bill in chunk)
            {
                var snapshot = new CreditCardBillSnapshot(
                    bill.DueDate, bill.ClosingDate, bill.TotalAmount, bill.CurrencyCode, bill.MinimumPayment, bill.RawJson);
                if (existing.TryGetValue(bill.Id.Trim(), out var row))
                {
                    // The id is unique in the whole database: a bill of another account is not this account's to change.
                    if (row.BankAccountId != account.Id) continue;
                    var dueBefore = row.DueDate;
                    if (!row.Refresh(snapshot, account.Currency, today, now)) continue;
                    changed.Add(dueBefore);
                    changed.Add(row.DueDate);
                    continue;
                }

                var stored = CreditCardBill.Create(connection.CoupleId, account.Id, bill.Id, snapshot, account.Currency, today, now);
                created.Add(stored);
                changed.Add(stored.DueDate);
            }

            if (created.Count > 0) await _sync.AddBillsAsync(created, ct);
        }

        return changed;
    }

    /// <summary>
    /// Returns the lines the automatic rules have not seen as settled yet (the new ones, and the ones the bank had
    /// not settled the last time and has now), and every line of the account the bank listed again, as it is now.
    /// </summary>
    private async Task<(IReadOnlyList<ArrivedBankLine> Arrived, IReadOnlyList<BankTransaction> ReadAgain)> UpsertTransactionsAsync(
        SyncRun run, BankConnection connection, BankAccount account, IReadOnlyList<PluggyTransaction> transactions, AiBudget ai, RunProgress progress,
        CancellationToken ct)
    {
        var incoming = transactions
            .Where(t => !string.IsNullOrWhiteSpace(t.Id) && t.Id.Trim().Length <= BankTransaction.MaxPluggyIdLength)
            .DistinctBy(t => t.Id.Trim())
            .ToList();
        var now = _clock.UtcNow;
        var added = 0;
        var updated = 0;
        var arrived = new List<ArrivedBankLine>();
        var readAgain = new List<BankTransaction>();

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
                    var waitedForTheBank = row.Status == BankTransactionStatus.Pending;
                    row.Refresh(snapshot, run.Id, now);
                    readAgain.Add(row);
                    updated++;
                    if (waitedForTheBank && row.Status == BankTransactionStatus.Posted)
                        arrived.Add(new ArrivedBankLine(row, transaction.PayerName));
                    continue;
                }

                var suggested = BankTransaction.TypeOf(transaction.Type, transaction.Amount) == BankTransactionType.Debit
                    ? await SuggestCategoryAsync(transaction, ai, ct)
                    : null;
                var line = BankTransaction.Create(
                    connection.CoupleId, connection.UserId, account.Id, transaction.Id, snapshot, suggested, run.Id, now);
                created.Add(line);
                arrived.Add(new ArrivedBankLine(line, transaction.PayerName));
                added++;
            }

            if (created.Count > 0) await _sync.AddTransactionsAsync(created, ct);
        }

        run.AddCounts(added, updated);
        progress.TransactionsNew += added;
        progress.TransactionsUpdated += updated;
        return (arrived, readAgain);
    }

    /// <summary>
    /// What the bank had not settled and no longer lists is gone at the bank (a pre-authorisation that fell, a
    /// pending purchase that got another id when it was settled): the line leaves the mirror, so that it does not
    /// wait for a review that can never confirm it. Only lines still pending at the bank AND still waiting for the
    /// review, on days this reading covered whole; a line someone confirmed or discarded is never touched. Returns
    /// the lines that leave. Only to be called with a listing Pluggy said was complete.
    /// </summary>
    private async Task<IReadOnlyList<BankTransaction>> RemoveVanishedAsync(
        BankAccount account, IReadOnlyList<PluggyTransaction> transactions, DateOnly from, DateOnly to, CancellationToken ct)
    {
        // The first day asked is left out: Pluggy cuts the days in UTC, and a transaction of that day could be
        // missing from the answer only because of where the day begins.
        var fromUtc = DateTime.SpecifyKind(from.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        // The last day asked is left out too. "dateTo" is documented as a day, inclusive, but typed as an instant:
        // were it cut at 00:00 UTC, what happened during that day would be missing from the answer without being
        // gone at the bank. That day is today (UTC): the reading of tomorrow covers it whole.
        var toUtc = DateTime.SpecifyKind(to.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        if (fromUtc >= toUtc) return [];

        var candidates = await _sync.GetUnsettledWaitingAsync(account.Id, fromUtc, toUtc, ct);
        if (candidates.Count == 0) return [];

        var listed = transactions
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => t.Id.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var vanished = candidates
            .Where(row => row.Status == BankTransactionStatus.Pending
                          && row.ReviewState == BankTransactionReviewState.Pending
                          && !listed.Contains(row.PluggyTransactionId))
            .ToList();
        if (vanished.Count > 0) _sync.RemoveTransactions(vanished);
        return vanished;
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
        if (string.IsNullOrWhiteSpace(description) || !ai.TryTake()) return TransactionCategories.Other;

        var asked = _clock.UtcNow;
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
        finally
        {
            ai.Spend(_clock.UtcNow - asked);
        }
    }

    private async Task SaveAsync(BankConnection connection, CancellationToken ct, Func<CancellationToken, Task>? decideAgain = null)
    {
        try
        {
            await _sync.SaveRunProgressAsync(connection, decideAgain, ct);
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
    /// <paramref name="whereItStopped"/> goes after the message in the diary of the run only (never on the connection).
    /// </summary>
    private async Task FailAsync(
        Guid runId, string code, string message, bool markConnection, CancellationToken ct, string whereItStopped = "")
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            _sync.DiscardChanges();
            var run = await _sync.FindRunForJobAsync(runId, ct);
            // Deleted with the connection, or already given a verdict by someone else (the recovery of another process).
            if (run is null || run.Status != SyncRunStatus.Running) return;

            var now = _clock.UtcNow;
            run.MarkFailed(code, message + whereItStopped, now);

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
                    await _sync.SaveRunProgressAsync(connection, decideAgain: null, ct);
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
        public SyncStoppedException(string code, string message, string whereItStopped = "") : base(message)
        {
            Code = code;
            WhereItStopped = whereItStopped;
        }

        public string Code { get; }

        /// <summary>What goes after the message in the diary of the run (the stage), when there is something to say.</summary>
        public string WhereItStopped { get; }
    }

    /// <summary>How far one run went, for its line in the log: where it is, and counts. Nothing of what it read.</summary>
    private sealed class RunProgress
    {
        public const string Finish = "finish";

        /// <summary>"prepare", then "item", "accounts", "transactions" and "bills" for each bank, then "finish".</summary>
        public string Stage { get; set; } = "prepare";

        /// <summary>Accounts whose listing of transactions came whole and was mirrored.</summary>
        public int AccountsRead { get; set; }

        public int TransactionsNew { get; set; }

        public int TransactionsUpdated { get; set; }

        /// <summary>Listings that stopped before Pluggy said they ended: nothing of them was stored.</summary>
        public int IncompleteListings { get; set; }
    }

    /// <summary>
    /// What one run may ask of the AI classifier: a number of calls, and a time that only the calls themselves use
    /// up (what Pluggy and the database take does not count).
    /// </summary>
    private sealed class AiBudget
    {
        private int _calls;
        private TimeSpan _timeLeft;

        public AiBudget(int calls, TimeSpan time)
        {
            _calls = calls;
            _timeLeft = time;
        }

        public bool TryTake()
        {
            if (_calls <= 0 || _timeLeft <= TimeSpan.Zero) return false;
            _calls--;
            return true;
        }

        /// <summary>Takes the time one call took out of what is left.</summary>
        public void Spend(TimeSpan taken)
        {
            if (taken > TimeSpan.Zero) _timeLeft -= taken;
        }

        public void Stop() => _calls = 0;
    }
}

/// <summary>
/// The days a run asks Pluggy for. The first time: today (in Brazil) minus the months the person chose. After that:
/// the day (in Brazil) of the last successful synchronisation minus <see cref="SyncConnectionService.OverlapDays"/>.
/// The last day is today in UTC, which is never behind Brazil: a purchase of this evening is already inside. Both
/// days go to Pluggy as they are (<c>dateFrom</c> and <c>dateTo</c> of GET /v2/transactions are inclusive).
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

    /// <summary>
    /// The first day to ask for one account, given the day (in Brazil) of the last transaction the mirror has of it.
    /// None: the account was never read (also a bank added later), so the whole history the person chose. Otherwise
    /// the earlier of two days: the window of the connection, and that last day minus the overlap, so that an
    /// account that went unread for weeks while the connection kept synchronising leaves no gap. Only the second is
    /// held at the history: the window of the connection is asked whole, even when it starts before the history
    /// (the connection went longer than that without a successful synchronisation, and those days were never read).
    /// </summary>
    public DateOnly FromFor(DateOnly? lastDayOfAccount)
    {
        if (lastDayOfAccount is not { } last) return HistoryFrom;
        var fromAccount = last.AddDays(-SyncConnectionService.OverlapDays);
        if (fromAccount < HistoryFrom) fromAccount = HistoryFrom;
        return fromAccount < From ? fromAccount : From;
    }
}
