using CoupleSync.Domain.ValueObjects;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Notification;

/// <summary>
/// Decides which alerts a new transaction raises. Alerts go to every active member of the couple (not only
/// to whoever entered the transaction), each member's own notification settings applying where a setting
/// exists. Repeating alerts are sent at most once per month: budget alerts per category and threshold,
/// the 30-day spending alert once; the large-transaction alert is raised once per transaction.
/// </summary>
public sealed class AlertPolicyService : IAlertPolicyService
{
    internal const decimal LargeTransactionThreshold = 500m;
    internal const string LargeTransactionKind = "LargeTransaction";
    internal const decimal LowBalanceThreshold = 3000m;

    private const string BudgetExceededKind = "BudgetExceeded";
    private const string BudgetWarningKind = "BudgetWarning";
    private const string LowBalanceKind = "LowBalance";

    private readonly IBudgetRepository _budgetRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly INotificationEventRepository _notificationEventRepository;
    private readonly ICoupleRepository _coupleRepository;
    private readonly INotificationSettingsRepository _notificationSettingsRepository;

    public AlertPolicyService(
        IBudgetRepository budgetRepository,
        ITransactionRepository transactionRepository,
        INotificationEventRepository notificationEventRepository,
        ICoupleRepository coupleRepository,
        INotificationSettingsRepository notificationSettingsRepository)
    {
        _budgetRepository = budgetRepository;
        _transactionRepository = transactionRepository;
        _notificationEventRepository = notificationEventRepository;
        _coupleRepository = coupleRepository;
        _notificationSettingsRepository = notificationSettingsRepository;
    }

    public async Task<IReadOnlyList<NotificationEvent>> EvaluatePostIngestAsync(
        Guid coupleId,
        Transaction newTransaction,
        IReadOnlyList<Transaction> recentTransactions,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        var events = new List<NotificationEvent>();
        var recipients = await GetRecipientsAsync(coupleId, newTransaction.UserId, ct);

        var currentMonth = BrazilTime.MonthOf(nowUtc);
        var (monthStart, monthEnd) = BrazilTime.MonthRangeUtc(currentMonth);
        var alertTypesThisMonth = await _notificationEventRepository.GetAlertTypesSinceAsync(coupleId, monthStart, ct);

        // Rule 1: LargeTransaction - one alert per transaction whose amount exceeds the threshold.
        if (CurrencyRules.IsBrl(newTransaction.Currency) && newTransaction.Amount > LargeTransactionThreshold)
        {
            var title = "Transação de valor alto";
            var body = $"Uma transação de {BrlFormat.Format(newTransaction.Amount)} foi registrada.";
            foreach (var recipient in recipients)
            {
                var settings = await SettingsOfAsync(recipient, coupleId, nowUtc, ct);
                if (settings.LargeTransactionEnabled)
                    events.Add(NotificationEvent.Create(coupleId, recipient, LargeTransactionKind, title, body, nowUtc));
            }
        }

        // Rule 2: LowBalance - total 30-day spend exceeds the threshold; once per month.
        var cutoff = nowUtc.AddDays(-30);
        var thirtyDaySpend = recentTransactions
            .Where(t => t.EventTimestampUtc >= cutoff && CurrencyRules.IsBrl(t.Currency))
            .Sum(t => t.Amount);

        if (thirtyDaySpend > LowBalanceThreshold && !LowBalanceAlreadySent(alertTypesThisMonth, currentMonth))
        {
            var dedupeAlertType = $"{LowBalanceKind}|{currentMonth}";
            var title = "Gastos altos nos últimos 30 dias";
            var body = $"Os gastos dos últimos 30 dias somam {BrlFormat.Format(thirtyDaySpend)} e passaram de {BrlFormat.Format(LowBalanceThreshold)}.";
            foreach (var recipient in recipients)
            {
                var settings = await SettingsOfAsync(recipient, coupleId, nowUtc, ct);
                if (settings.LowBalanceEnabled)
                    events.Add(NotificationEvent.Create(coupleId, recipient, dedupeAlertType, title, body, nowUtc));
            }
        }

        // Rule 3: BillReminder - time-based, not triggered post-ingest. Skip for V1.

        // Rule 4: budget alerts for the transaction's category in the current month.
        var budgetAlert = await CheckBudgetAsync(coupleId, newTransaction, currentMonth, monthStart, monthEnd, alertTypesThisMonth, ct);
        if (budgetAlert is not null)
        {
            foreach (var recipient in recipients)
                events.Add(NotificationEvent.Create(coupleId, recipient, budgetAlert.AlertType, budgetAlert.Title, budgetAlert.Body, nowUtc));
        }

        return events;
    }

    private sealed record BudgetAlert(string AlertType, string Title, string Body);

    private async Task<BudgetAlert?> CheckBudgetAsync(
        Guid coupleId,
        Transaction transaction,
        string currentMonth,
        DateTime monthStart,
        DateTime monthEnd,
        IReadOnlyList<string> alertTypesThisMonth,
        CancellationToken ct)
    {
        var plan = await _budgetRepository.GetByMonthAsync(coupleId, currentMonth, ct);
        if (plan is null)
            return null;

        var category = TransactionCategories.NormalizeOrOther(transaction.Category);
        var allocation = plan.Allocations
            .FirstOrDefault(a => TransactionCategories.NormalizeOrOther(a.Category) == category);
        if (allocation is null)
            return null;

        var exceededSent = BudgetAlertAlreadySent(alertTypesThisMonth, BudgetExceededKind, category, currentMonth);
        var warningSent = BudgetAlertAlreadySent(alertTypesThisMonth, BudgetWarningKind, category, currentMonth);
        if (exceededSent && warningSent)
            return null;

        var actualSpentMap = await _transactionRepository.GetActualSpentByCategoryAsync(
            coupleId, monthStart, monthEnd, ct);
        var actualSpent = actualSpentMap.GetValueOrDefault(category, 0m);
        var label = TransactionCategories.Label(category);
        var spent = BrlFormat.Format(actualSpent);
        var allocated = BrlFormat.Format(allocation.AllocatedAmount);

        if (actualSpent > allocation.AllocatedAmount)
        {
            if (exceededSent)
                return null;

            return new BudgetAlert(
                $"{BudgetExceededKind}|{category}|{currentMonth}",
                $"Orçamento de {label} estourado",
                $"Você gastou {spent} de {allocated} no orçamento de {label} este mês.");
        }

        // The 80-99% band only (strictly below the limit, so it never overlaps with the exceeded alert).
        if (!warningSent
            && actualSpent >= allocation.AllocatedAmount * 0.8m
            && actualSpent < allocation.AllocatedAmount)
        {
            return new BudgetAlert(
                $"{BudgetWarningKind}|{category}|{currentMonth}",
                $"Atenção: orçamento de {label}",
                $"Você já usou mais de 80% do orçamento de {label} este mês ({spent} de {allocated}).");
        }

        return null;
    }

    /// <summary>
    /// True when this month already has the alert. The category part of stored keys is compared by its canonical
    /// key, so alerts recorded before categories were normalised ("Alimentação", "alimentacao") still count.
    /// </summary>
    private static bool BudgetAlertAlreadySent(
        IReadOnlyList<string> alertTypesThisMonth, string kind, string category, string month)
    {
        foreach (var alertType in alertTypesThisMonth)
        {
            var parts = alertType.Split('|');
            if (parts.Length == 3
                && parts[0] == kind
                && parts[2] == month
                && TransactionCategories.NormalizeOrOther(parts[1]) == category)
                return true;
        }

        return false;
    }

    /// <summary>Also counts the plain "LowBalance" type used before the monthly key existed.</summary>
    private static bool LowBalanceAlreadySent(IReadOnlyList<string> alertTypesThisMonth, string month)
        => alertTypesThisMonth.Contains(LowBalanceKind) || alertTypesThisMonth.Contains($"{LowBalanceKind}|{month}");

    /// <summary>Every active member of the couple, plus the author (always, even if the couple lookup fails).</summary>
    private async Task<IReadOnlyList<Guid>> GetRecipientsAsync(Guid coupleId, Guid authorId, CancellationToken ct)
    {
        var couple = await _coupleRepository.FindByIdWithMembersAsync(coupleId, ct);
        var recipients = new List<Guid>();
        if (couple is not null)
            recipients.AddRange(couple.Members.Where(m => m.IsActive).Select(m => m.Id));

        if (!recipients.Contains(authorId))
            recipients.Add(authorId);

        return recipients;
    }

    // No row in notification_settings means the user never changed anything: the defaults (every alert
    // enabled) apply, exactly as GET /notifications/settings reports them.
    private async Task<NotificationSettings> SettingsOfAsync(Guid userId, Guid coupleId, DateTime nowUtc, CancellationToken ct)
        => await _notificationSettingsRepository.GetByUserIdAsync(userId, coupleId, ct)
           ?? NotificationSettings.Create(userId, coupleId, nowUtc);
}
