using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Notification;

public sealed class AlertPolicyService : IAlertPolicyService
{
    private const decimal LargeTransactionThreshold = 500m;
    internal const decimal LowBalanceThreshold = 3000m;

    private readonly IBudgetRepository? _budgetRepository;
    private readonly ITransactionRepository? _transactionRepository;
    private readonly INotificationEventRepository? _notificationEventRepository;

    /// <summary>Parameterless constructor used by unit tests (budget check skipped).</summary>
    public AlertPolicyService() { }

    public AlertPolicyService(
        IBudgetRepository budgetRepository,
        ITransactionRepository transactionRepository,
        INotificationEventRepository notificationEventRepository)
    {
        _budgetRepository = budgetRepository;
        _transactionRepository = transactionRepository;
        _notificationEventRepository = notificationEventRepository;
    }

    public async Task<IReadOnlyList<NotificationEvent>> EvaluatePostIngestAsync(
        Guid coupleId,
        Guid userId,
        Transaction newTransaction,
        IReadOnlyList<Transaction> recentTransactions,
        NotificationSettings settings,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        var events = new List<NotificationEvent>();

        // Rule 1: LargeTransaction — single transaction exceeds threshold
        if (newTransaction.Amount > LargeTransactionThreshold && settings.LargeTransactionEnabled)
        {
            events.Add(NotificationEvent.Create(
                coupleId,
                userId,
                "LargeTransaction",
                "Large transaction detected",
                $"A transaction of {newTransaction.Amount:F2} {newTransaction.Currency} was detected.",
                nowUtc));
        }

        // Rule 2: LowBalance — total 30-day spend exceeds threshold
        var cutoff = nowUtc.AddDays(-30);
        var thirtyDaySpend = recentTransactions
            .Where(t => t.EventTimestampUtc >= cutoff)
            .Sum(t => t.Amount);

        if (thirtyDaySpend > LowBalanceThreshold && settings.LowBalanceEnabled)
        {
            events.Add(NotificationEvent.Create(
                coupleId,
                userId,
                "LowBalance",
                "High 30-day spending alert",
                $"Your 30-day total spending of {thirtyDaySpend:F2} has exceeded the threshold.",
                nowUtc));
        }

        // Rule 3: BillReminder — time-based, not triggered post-ingest. Skip for V1.

        // Rule 4: BudgetExceeded — actual spending for the transaction's category exceeds
        //         the allocated amount for the current month.
        if (_budgetRepository is not null
            && _transactionRepository is not null
            && _notificationEventRepository is not null)
        {
            var budgetExceededEvent = await CheckBudgetExceededAsync(
                coupleId, userId, newTransaction, nowUtc, ct);

            if (budgetExceededEvent is not null)
                events.Add(budgetExceededEvent);
        }

        return events;
    }

    private async Task<NotificationEvent?> CheckBudgetExceededAsync(
        Guid coupleId,
        Guid userId,
        Transaction transaction,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var currentMonth = $"{nowUtc.Year:D4}-{nowUtc.Month:D2}";
        var plan = await _budgetRepository!.GetByMonthAsync(coupleId, currentMonth, ct);

        if (plan is null)
            return null;

        var category = transaction.Category;
        var allocation = plan.Allocations
            .FirstOrDefault(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase));

        if (allocation is null)
            return null;

        // Only fire once per category per month.
        var dedupeAlertType = $"BudgetExceeded|{category}|{currentMonth}";
        var alreadySent = await _notificationEventRepository!.ExistsByAlertTypeAsync(coupleId, dedupeAlertType, ct);
        if (alreadySent)
            return null;

        var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthEnd = monthStart.AddMonths(1);
        var actualSpentMap = await _transactionRepository!.GetActualSpentByCategoryAsync(
            coupleId, monthStart, monthEnd, ct);

        var actualSpent = actualSpentMap.GetValueOrDefault(category, 0m);

        if (actualSpent <= allocation.AllocatedAmount)
            return null;

        var title = $"Budget exceeded: {category}";
        var body = $"Spent {actualSpent:F2} {allocation.Currency} of {allocation.AllocatedAmount:F2} {allocation.Currency} {category} budget.";

        return NotificationEvent.Create(coupleId, userId, dedupeAlertType, title, body, nowUtc);
    }
}
