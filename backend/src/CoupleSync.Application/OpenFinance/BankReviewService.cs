using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Application.OpenFinance;

/// <summary>
/// The review of what came from the bank: nothing of the mirror becomes a transaction of the app without someone of
/// the group confirming it here. Anyone of the group reviews; the transaction created belongs to who connected the
/// account. Only expenses in this phase.
/// </summary>
public sealed class BankReviewService
{
    public const string NotPostedCode = "TRANSACTION_NOT_POSTED";
    public const string NotFoundCode = "BANK_TRANSACTION_NOT_FOUND";
    public const string NotPendingCode = "BANK_TRANSACTION_NOT_PENDING";
    public const string InvalidSelectionCode = "INVALID_SELECTION";
    public const string ConflictCode = "BANK_REVIEW_CONFLICT";
    public const string InvalidMonthCode = "INVALID_MONTH";

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

    /// <summary>The expenses of a month of Brazil ("yyyy-MM"; the current one when absent) waiting, and the discarded ones apart.</summary>
    public async Task<BankReviewDto> GetReviewAsync(Guid coupleId, string? month, CancellationToken ct)
    {
        var (key, from) = ParseMonth(month);
        var rows = await _sync.GetReviewExpensesAsync(coupleId, from, from.AddMonths(1), ct);
        var names = await _sync.GetAccountNamesAsync(coupleId, ct);
        var pendingDays = await _sync.GetPendingExpenseDaysAsync(coupleId, ct);

        var expenses = rows.Where(r => r.ReviewState == BankTransactionReviewState.Pending).ToList();
        var discarded = rows.Where(r => r.ReviewState == BankTransactionReviewState.Discarded).ToList();

        var byMonth = pendingDays
            .GroupBy(d => $"{d.Year:D4}-{d.Month:D2}", StringComparer.Ordinal)
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => new BankReviewMonthDto(g.Key, g.Count()))
            .ToList();

        return new BankReviewDto(
            key,
            Order(expenses).Select(r => MapLine(r, names)).ToList(),
            Order(discarded).Select(r => MapLine(r, names)).ToList(),
            // Sums in reais count only reais: a purchase in another currency is listed, never added.
            expenses.Where(r => CurrencyRules.IsBrl(r.Currency)).Sum(r => r.AbsoluteAmount),
            pendingDays.Count,
            byMonth);
    }

    public async Task<BankReviewConfirmResult> ConfirmAsync(
        Guid coupleId, IReadOnlyList<ConfirmExpenseInput>? expenses, IReadOnlyList<Guid>? discard, CancellationToken ct)
    {
        expenses ??= [];
        var discardIds = (discard ?? []).Distinct().ToList();
        var expenseIds = expenses.Select(e => e.Id).ToList();

        if (expenseIds.Count == 0 && discardIds.Count == 0)
            throw new UnprocessableEntityException(InvalidSelectionCode, "Selecione pelo menos um lançamento.");
        if (expenseIds.Count != expenseIds.Distinct().Count())
            throw new UnprocessableEntityException(InvalidSelectionCode, "O mesmo lançamento foi enviado mais de uma vez.");
        if (expenseIds.Intersect(discardIds).Any())
            throw new UnprocessableEntityException(InvalidSelectionCode, "Um lançamento não pode ser confirmado e descartado ao mesmo tempo.");
        if (expenseIds.Count > MaxLinesPerRequest || discardIds.Count > MaxLinesPerRequest)
            throw new UnprocessableEntityException(InvalidSelectionCode, $"Envie no máximo {MaxLinesPerRequest} lançamentos por vez.");

        var allIds = expenseIds.Concat(discardIds).ToList();
        var rows = (await _sync.FindByIdsAsync(allIds, coupleId, ct)).ToDictionary(r => r.Id);
        // A line of another group does not exist for this one.
        if (allIds.Any(id => !rows.ContainsKey(id)))
            throw new NotFoundException(NotFoundCode, "Lançamento do banco não encontrado.");

        if (allIds.Any(id => !rows[id].IsExpense))
            throw new UnprocessableEntityException(InvalidSelectionCode, "Entradas não são confirmadas como despesa.");

        // What the bank has not settled yet may still change or vanish: it cannot become a transaction.
        if (expenseIds.Any(id => rows[id].Status == BankTransactionStatus.Pending))
            throw new UnprocessableEntityException(
                NotPostedCode,
                "Este lançamento ainda está pendente no banco. Ele poderá ser confirmado quando o banco o efetivar.");

        if (expenseIds.Any(id => rows[id].ReviewState is not (BankTransactionReviewState.Pending or BankTransactionReviewState.Confirmed))
            || discardIds.Any(id => rows[id].ReviewState is not (BankTransactionReviewState.Pending or BankTransactionReviewState.Discarded)))
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
            var ingest = TransactionEventIngest.Create(
                coupleId, row.UserId, TransactionEventIngest.OpenFinanceBank, row.AbsoluteAmount, row.Currency,
                timestamp, description, merchant, rawNotificationTextRedacted: null, now);
            var transaction = Transaction.Create(
                coupleId, row.UserId, fingerprint, bankName, row.AbsoluteAmount, row.Currency, timestamp,
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
            // Another request confirmed the same line first (the unique fingerprint let one through), or the lines
            // went away with the person who left the group. Nothing of this request was stored.
            throw new ConflictException(ConflictCode, "A revisão mudou enquanto era confirmada. Atualize e tente de novo.");
        }

        await RaiseAlertsAsync(coupleId, transactions, ct);

        return new BankReviewConfirmResult(created, discarded, alreadyConfirmed, skipped);
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
            var alertEvents = await _alertPolicyService.EvaluatePostImportAsync(coupleId, created, recentTransactions, nowUtc, ct);
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

    /// <summary>Discarded lines go back to waiting. Lines in any other state stay as they are.</summary>
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
        foreach (var row in rows.Where(r => r.ReviewState is BankTransactionReviewState.Discarded or BankTransactionReviewState.Ignored))
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

    private static BankReviewLineDto MapLine(BankTransaction row, IReadOnlyDictionary<Guid, BankAccountNames> names)
    {
        names.TryGetValue(row.BankAccountId, out var account);
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
            row.InstallmentTotal);
    }
}
