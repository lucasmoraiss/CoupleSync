using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Application.OpenFinance;

/// <summary>
/// The review of what came from the bank: nothing of the mirror becomes a transaction of the app without someone of
/// the group confirming it here. Anyone of the group reviews; the transaction created belongs to who connected the
/// account. Only expenses in this phase. A line that looks the same as a transaction the app already has is
/// answered here too: "it is the same" (reconcile: nothing is created) or "it is another one" (confirmed as new).
/// </summary>
public sealed class BankReviewService
{
    public const string NotPostedCode = "TRANSACTION_NOT_POSTED";
    public const string NotFoundCode = "BANK_TRANSACTION_NOT_FOUND";
    public const string NotPendingCode = "BANK_TRANSACTION_NOT_PENDING";
    public const string InvalidSelectionCode = "INVALID_SELECTION";
    public const string ConflictCode = "BANK_REVIEW_CONFLICT";
    public const string InvalidMonthCode = "INVALID_MONTH";
    public const string NotMatchedCode = "BANK_TRANSACTION_NOT_MATCHED";
    public const string MatchTakenCode = "MATCHED_TRANSACTION_TAKEN";

    private const string NotMatchedMessage =
        "Este lançamento não tem mais uma transação parecida para conciliar. Atualize a revisão e tente de novo.";
    private const string MatchTakenMessage =
        "A transação parecida já está ligada a outro lançamento do banco. Confirme este como outra despesa ou descarte.";

    /// <summary>Lines of one request, at most (the app sends batches of 200).</summary>
    public const int MaxLinesPerRequest = 500;

    private const string FallbackBankName = "Open Finance";
    private const int MaxBankNameLength = 64;

    private readonly IBankSyncRepository _sync;
    private readonly ITransactionRepository _transactions;
    private readonly INotificationCaptureRepository _ingests;
    private readonly IFingerprintGenerator _fingerprints;
    private readonly IDateTimeProvider _clock;
    private readonly IAlertPolicyService _alertPolicyService;
    private readonly INotificationEventRepository _notificationEvents;
    private readonly ILogger<BankReviewService> _logger;

    public BankReviewService(
        IBankSyncRepository sync,
        ITransactionRepository transactions,
        INotificationCaptureRepository ingests,
        IFingerprintGenerator fingerprints,
        IDateTimeProvider clock,
        IAlertPolicyService alertPolicyService,
        INotificationEventRepository notificationEvents,
        ILogger<BankReviewService> logger)
    {
        _sync = sync;
        _transactions = transactions;
        _ingests = ingests;
        _fingerprints = fingerprints;
        _clock = clock;
        _alertPolicyService = alertPolicyService;
        _notificationEvents = notificationEvents;
        _logger = logger;
    }

    /// <summary>
    /// The identity of the transaction created from a bank transaction: only the group and the id Pluggy gives it,
    /// so that confirming the same bank transaction again can never store a second one.
    /// </summary>
    public static string FingerprintOf(IFingerprintGenerator generator, Guid coupleId, string pluggyTransactionId)
        => generator.Generate(coupleId, TransactionEventIngest.OpenFinanceBank, 0m, CurrencyRules.Brl, DateTime.UnixEpoch, pluggyTransactionId);

    /// <summary>
    /// The expenses of a month of Brazil ("yyyy-MM"; the current one when absent) waiting (each with the transaction
    /// it looks the same as, when there is one), and apart the discarded and the automatically ignored ones.
    /// </summary>
    public async Task<BankReviewDto> GetReviewAsync(Guid coupleId, string? month, CancellationToken ct)
    {
        var (key, from) = ParseMonth(month);
        var rows = await _sync.GetReviewExpensesAsync(coupleId, from, from.AddMonths(1), ct);
        var ignored = await _sync.GetIgnoredAsync(coupleId, from, from.AddMonths(1), ct);
        var names = await _sync.GetAccountNamesAsync(coupleId, ct);
        var pending = await _sync.GetPendingExpensesAsync(coupleId, ct);
        var ignoredDays = await _sync.GetIgnoredDaysAsync(coupleId, ct);

        var expenses = rows.Where(r => r.ReviewState == BankTransactionReviewState.Pending).ToList();
        var discarded = rows.Where(r => r.CountsAsDiscarded).ToList();
        var matches = await _sync.GetTransactionSummariesAsync(
            expenses.Where(r => r.MatchedTransactionId is not null).Select(r => r.MatchedTransactionId!.Value).ToList(), coupleId, ct);
        var expenseLines = Order(expenses).Select(r => MapLine(r, names, matches)).ToList();

        var byMonth = pending
            .GroupBy(p => $"{p.Day.Year:D4}-{p.Day.Month:D2}", StringComparer.Ordinal)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BankReviewMonthDto(g.Key, g.Count()))
            .ToList();
        var ignoredByMonth = ignoredDays
            .GroupBy(day => $"{day.Year:D4}-{day.Month:D2}", StringComparer.Ordinal)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BankReviewIgnoredMonthDto(g.Key, g.Count()))
            .ToList();

        return new BankReviewDto(
            key,
            expenseLines,
            Order(discarded).Select(r => MapLine(r, names)).ToList(),
            // Sums in reais count only reais: a purchase in another currency is listed, never added.
            expenses.Where(r => CurrencyRules.IsBrl(r.Currency)).Sum(r => r.AbsoluteAmount),
            // "N to review" is what can be confirmed. What cannot (pending at the bank, without a value, in another
            // currency) is still listed in its month, and counted there, to be discarded.
            pending.Count(p => BankTransaction.CanBecomeExpense(p.Status, p.Amount, p.Currency)),
            byMonth,
            Order(ignored).Select(r => MapLine(r, names)).ToList(),
            expenseLines.Count(l => l.Match is not null),
            ignoredDays.Count,
            ignoredByMonth);
    }

    /// <summary>
    /// <paramref name="expenses"/> become transactions; <paramref name="asNew"/> too (the answer "it is another one"
    /// to a suspected duplicate, with the suggested category); <paramref name="reconcile"/> are the answer "it is the
    /// same": the line points to the transaction the app already had and nothing is created;
    /// <paramref name="discard"/> are discarded. All or nothing.
    /// </summary>
    public async Task<BankReviewConfirmResult> ConfirmAsync(
        Guid coupleId,
        IReadOnlyList<ConfirmExpenseInput>? expenses,
        IReadOnlyList<Guid>? discard,
        IReadOnlyList<Guid>? reconcile,
        IReadOnlyList<Guid>? asNew,
        CancellationToken ct)
    {
        expenses ??= [];
        asNew ??= [];
        var reconcileIds = (reconcile ?? []).ToList();
        var discardIds = (discard ?? []).Distinct().ToList();
        var typedIds = expenses.Select(e => e.Id).ToList();

        if (typedIds.Count == 0 && discardIds.Count == 0 && reconcileIds.Count == 0 && asNew.Count == 0)
            throw new UnprocessableEntityException(InvalidSelectionCode, "Selecione pelo menos um lançamento.");
        if (typedIds.Count > MaxLinesPerRequest || discardIds.Count > MaxLinesPerRequest
            || reconcileIds.Count > MaxLinesPerRequest || asNew.Count > MaxLinesPerRequest)
        {
            throw new UnprocessableEntityException(InvalidSelectionCode, $"Envie no máximo {MaxLinesPerRequest} lançamentos por vez.");
        }

        if (typedIds.Count != typedIds.Distinct().Count()
            || reconcileIds.Count != reconcileIds.Distinct().Count()
            || asNew.Count != asNew.Distinct().Count())
        {
            throw new UnprocessableEntityException(InvalidSelectionCode, "O mesmo lançamento foi enviado mais de uma vez.");
        }

        if (typedIds.Intersect(discardIds).Any())
            throw new UnprocessableEntityException(InvalidSelectionCode, "Um lançamento não pode ser confirmado e descartado ao mesmo tempo.");
        if (reconcileIds.Intersect(typedIds).Any() || reconcileIds.Intersect(discardIds).Any() || reconcileIds.Intersect(asNew).Any()
            || asNew.Intersect(typedIds).Any() || asNew.Intersect(discardIds).Any())
        {
            throw new UnprocessableEntityException(InvalidSelectionCode, "Um lançamento só pode ter um destino por vez.");
        }

        // "It is another one" is a confirmation like any other, with what the bank and the table suggest.
        expenses = expenses.Concat(asNew.Select(id => new ConfirmExpenseInput(id, null, null))).ToList();
        var expenseIds = expenses.Select(e => e.Id).ToList();

        var allIds = expenseIds.Concat(discardIds).Concat(reconcileIds).ToList();
        var rows = (await _sync.FindByIdsAsync(allIds, coupleId, ct)).ToDictionary(r => r.Id);
        // A line of another group does not exist for this one.
        if (allIds.Any(id => !rows.ContainsKey(id)))
            throw new NotFoundException(NotFoundCode, "Lançamento do banco não encontrado.");

        if (allIds.Any(id => !rows[id].IsExpense))
            throw new UnprocessableEntityException(InvalidSelectionCode, "Entradas não são confirmadas como despesa.");

        // What the bank has not settled yet may still change or vanish: it cannot become a transaction.
        if (expenseIds.Concat(reconcileIds).Any(id => rows[id].Status == BankTransactionStatus.Pending))
            throw new UnprocessableEntityException(
                NotPostedCode,
                "Este lançamento ainda está pendente no banco. Ele poderá ser confirmado quando o banco o efetivar.");

        if (expenseIds.Any(id => rows[id].ReviewState is not (BankTransactionReviewState.Pending or BankTransactionReviewState.Confirmed))
            || discardIds.Any(id => rows[id].ReviewState != BankTransactionReviewState.Pending && !rows[id].CountsAsDiscarded)
            || reconcileIds.Any(id => rows[id].ReviewState is not (BankTransactionReviewState.Pending or BankTransactionReviewState.Reconciled)))
        {
            throw new ConflictException(NotPendingCode, "Este lançamento já foi revisado. Atualize a revisão e tente de novo.");
        }

        // One read for the whole request: which of these bank transactions already have their transaction.
        var fingerprints = expenseIds
            .Where(id => rows[id].ReviewState == BankTransactionReviewState.Pending)
            .ToDictionary(id => id, id => FingerprintOf(_fingerprints, coupleId, rows[id].PluggyTransactionId));
        var existing = await _sync.FindTransactionIdsByFingerprintsAsync(fingerprints.Values.Distinct(StringComparer.Ordinal).ToList(), coupleId, ct);

        var names = await _sync.GetAccountNamesAsync(coupleId, ct);
        var now = _clock.UtcNow;
        var ingests = new List<TransactionEventIngest>();
        var transactions = new List<Transaction>();
        var created = new List<BankReviewCreatedDto>();
        var skipped = new List<Guid>();
        var skippedOtherCurrency = new List<Guid>();
        var alreadyConfirmed = 0;

        foreach (var input in expenses)
        {
            var row = rows[input.Id];
            if (row.ReviewState == BankTransactionReviewState.Confirmed)
            {
                alreadyConfirmed++; // confirmed before (the same request sent twice): never a second transaction
                continue;
            }

            // A line without a value cannot be an expense. It does not hold the others back ("Selecionar tudo"):
            // it stays waiting, to be discarded, and the answer says it was skipped.
            if (row.AbsoluteAmount <= 0)
            {
                skipped.Add(row.Id);
                continue;
            }

            // Every sum and every list of the app is in reais, and every other way of entering a transaction
            // refuses another currency: in this phase a purchase in another currency is skipped the same way.
            if (!CurrencyRules.IsBrl(row.Currency))
            {
                skipped.Add(row.Id);
                skippedOtherCurrency.Add(row.Id);
                continue;
            }

            var fingerprint = fingerprints[row.Id];
            if (existing.TryGetValue(fingerprint, out var existingId))
            {
                // The transaction of this bank transaction is already there: the line points to it, nothing is added.
                row.Confirm(existingId, now);
                alreadyConfirmed++;
                continue;
            }

            var description = !string.IsNullOrWhiteSpace(input.Description)
                ? input.Description.Trim()
                : row.Description ?? row.DescriptionRaw;
            var merchant = row.MerchantName ?? row.Description ?? row.DescriptionRaw;
            var category = TransactionCategories.TryNormalize(input.Category)
                ?? TransactionCategories.TryNormalize(row.SuggestedCategory)
                ?? TransactionCategories.Other;
            var bankName = names.TryGetValue(row.BankAccountId, out var accountNames) && !string.IsNullOrWhiteSpace(accountNames.BankName)
                ? accountNames.BankName
                : FallbackBankName;
            if (bankName.Length > MaxBankNameLength) bankName = bankName[..MaxBankNameLength];
            var timestamp = row.EventTimestampUtc();

            // Whoever confirms, the expense belongs to who connected the account.
            // Only reais get here (anything else was skipped above).
            var ingest = TransactionEventIngest.Create(
                coupleId, row.UserId, TransactionEventIngest.OpenFinanceBank, row.AbsoluteAmount, CurrencyRules.Brl,
                timestamp, description, merchant, rawNotificationTextRedacted: null, now);
            var transaction = Transaction.Create(
                coupleId, row.UserId, fingerprint, bankName, row.AbsoluteAmount, CurrencyRules.Brl, timestamp,
                description, merchant, category, ingest.Id, now, TransactionSource.OpenFinance);

            ingests.Add(ingest);
            transactions.Add(transaction);
            row.Confirm(transaction.Id, now);
            created.Add(new BankReviewCreatedDto(row.Id, transaction.Id));
        }

        var discarded = new List<Guid>();
        foreach (var id in discardIds)
        {
            var row = rows[id];
            if (row.ReviewState == BankTransactionReviewState.Pending) row.Discard(now);
            discarded.Add(id);
        }

        var reconciled = await ReconcileAsync(coupleId, reconcileIds.Select(id => rows[id]).ToList(), now, ct);

        try
        {
            if (transactions.Count > 0)
            {
                await _ingests.AddIngestEventsRangeAsync(ingests, ct);
                await _transactions.AddTransactionsRangeAsync(transactions, ct);
            }

            // One unit of work (the repositories share the context): lines and transactions are stored together or not at all.
            await _sync.SaveChangesAsync(ct);
        }
        catch (DataStoreException ex) when (ex is UniqueViolationException or ConcurrencyConflictException or ForeignKeyViolationException)
        {
            // Another request confirmed the same line first (the unique fingerprint let one through), someone
            // reviewed one of the lines after this request read it (the review state is the concurrency token of
            // the line: confirming and discarding the same line at once, one of the two loses here), or the lines
            // went away with the person who left the group. Nothing of this request was stored.
            throw new ConflictException(ConflictCode, "A revisão mudou enquanto era confirmada. Atualize e tente de novo.");
        }

        await RaiseAlertsAsync(coupleId, transactions, ct);

        return new BankReviewConfirmResult(created, discarded, alreadyConfirmed, skipped, skippedOtherCurrency, reconciled);
    }

    /// <summary>
    /// "It is the same": each line points to the transaction it was suspected of repeating, and that transaction
    /// takes from the bank only the merchant and the category it did not have. Nothing is created and nothing is
    /// saved here (the caller saves everything together). A line reconciled before answers what it answered then.
    /// </summary>
    private async Task<List<BankReviewReconciledDto>> ReconcileAsync(
        Guid coupleId, IReadOnlyList<BankTransaction> lines, DateTime now, CancellationToken ct)
    {
        var reconciled = new List<BankReviewReconciledDto>();
        var taken = new HashSet<Guid>();

        foreach (var row in lines)
        {
            if (row.ReviewState == BankTransactionReviewState.Reconciled)
            {
                // The same request sent twice. Without the link, the transaction was deleted at the very moment of
                // the answer: the line counts as discarded (it is listed there and can be restored) and there is
                // nothing to answer for it.
                if (row.LinkedTransactionId is { } linked) reconciled.Add(new BankReviewReconciledDto(row.Id, linked));
                continue;
            }

            if (row.MatchedTransactionId is not { } matchedId)
                throw new ConflictException(NotMatchedCode, NotMatchedMessage);

            // Read through the group of the token: a transaction of another group does not exist for this one.
            var transaction = await _transactions.GetByIdAsync(matchedId, coupleId, ct)
                ?? throw new ConflictException(NotMatchedCode, NotMatchedMessage);

            // One transaction is one line of the bank: a second line can only be another expense.
            if (!taken.Add(transaction.Id) || await _sync.FindByLinkedTransactionAsync(transaction.Id, coupleId, ct) is not null)
                throw new ConflictException(MatchTakenCode, MatchTakenMessage);

            transaction.FillMissing(row.MerchantName ?? row.Description ?? row.DescriptionRaw, row.SuggestedCategory);
            row.Reconcile(transaction.Id, now);
            reconciled.Add(new BankReviewReconciledDto(row.Id, transaction.Id));
        }

        return reconciled;
    }

    /// <summary>
    /// The same evaluation every other way of entering a transaction runs (manual, notification, statement import),
    /// once for the whole confirmation: many lines are one summary, not one push per line. Never fails the
    /// confirmation: the transactions are already stored.
    /// </summary>
    private async Task RaiseAlertsAsync(Guid coupleId, IReadOnlyList<Transaction> created, CancellationToken ct)
    {
        if (created.Count == 0) return;

        try
        {
            var nowUtc = _clock.UtcNow;
            var recentTransactions = await _transactions.GetRecentByCoupleAsync(coupleId, nowUtc.AddDays(-30), ct);
            var alertEvents = await _alertPolicyService.EvaluatePostBankReviewAsync(coupleId, created, recentTransactions, nowUtc, ct);
            if (alertEvents.Count > 0)
            {
                await _notificationEvents.AddRangeAsync(alertEvents, ct);
                await _notificationEvents.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Alert policy evaluation failed for couple {CoupleId} ({ExceptionType}).", coupleId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Discarded lines (<see cref="BankTransaction.CountsAsDiscarded"/>) and automatically ignored ones go back to
    /// waiting, and from then on no automatic rule decides about them again. Lines in any other state stay as they are.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RestoreAsync(Guid coupleId, IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        var wanted = (ids ?? []).Distinct().ToList();
        if (wanted.Count == 0)
            throw new UnprocessableEntityException(InvalidSelectionCode, "Selecione pelo menos um lançamento.");
        if (wanted.Count > MaxLinesPerRequest)
            throw new UnprocessableEntityException(InvalidSelectionCode, $"Envie no máximo {MaxLinesPerRequest} lançamentos por vez.");

        var rows = await _sync.FindByIdsAsync(wanted, coupleId, ct);
        if (rows.Count != wanted.Count)
            throw new NotFoundException(NotFoundCode, "Lançamento do banco não encontrado.");

        var now = _clock.UtcNow;
        var restored = new List<Guid>();
        foreach (var row in rows.Where(r => r.CountsAsDiscarded || r.ReviewState == BankTransactionReviewState.Ignored))
        {
            row.Restore(now);
            restored.Add(row.Id);
        }

        try
        {
            await _sync.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            throw new ConflictException(ConflictCode, "A revisão mudou enquanto era alterada. Atualize e tente de novo.");
        }

        return restored;
    }

    private (string Key, DateOnly First) ParseMonth(string? month)
    {
        if (string.IsNullOrWhiteSpace(month))
        {
            var today = BrazilTime.ToLocal(_clock.UtcNow);
            return ($"{today.Year:D4}-{today.Month:D2}", new DateOnly(today.Year, today.Month, 1));
        }

        var text = month.Trim();
        if (text.Length == 7 && text[4] == '-'
            && int.TryParse(text.AsSpan(0, 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var year)
            && int.TryParse(text.AsSpan(5, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && year is >= 2000 and <= 2100 && number is >= 1 and <= 12)
        {
            return (text, new DateOnly(year, number, 1));
        }

        throw new BadRequestException(InvalidMonthCode, "O mês deve estar no formato AAAA-MM.");
    }

    private static IEnumerable<BankTransaction> Order(IEnumerable<BankTransaction> rows)
        => rows.OrderByDescending(r => r.LocalDate).ThenByDescending(r => r.Date).ThenBy(r => r.Id);

    private static BankReviewLineDto MapLine(
        BankTransaction row,
        IReadOnlyDictionary<Guid, BankAccountNames> names,
        IReadOnlyDictionary<Guid, MatchedTransactionSummary>? matches = null)
    {
        names.TryGetValue(row.BankAccountId, out var account);
        MatchedTransactionSummary? match = null;
        if (row.MatchedTransactionId is { } matchedId) matches?.TryGetValue(matchedId, out match);
        return new BankReviewLineDto(
            row.Id,
            row.LocalDate,
            row.MerchantName,
            row.Description ?? row.DescriptionRaw,
            row.AbsoluteAmount,
            row.Currency,
            TransactionCategories.NormalizeOrOther(row.SuggestedCategory),
            row.Status.ToString(),
            account?.BankName ?? string.Empty,
            account?.AccountName ?? string.Empty,
            row.InstallmentNumber,
            row.InstallmentTotal,
            row.Type.ToString(),
            row.ReviewState == BankTransactionReviewState.Ignored ? row.AutoReason : null,
            match is null
                ? null
                : new BankReviewMatchDto(
                    match.Id,
                    match.Source.ToString(),
                    match.Description,
                    match.Merchant,
                    match.Amount,
                    match.Currency,
                    BankReconciliationRules.LocalDayOf(match.EventTimestampUtc, match.Source)));
    }
}
