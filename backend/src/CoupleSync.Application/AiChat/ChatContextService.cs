using CoupleSync.Domain.ValueObjects;
using System.Globalization;
using System.Text;
using CoupleSync.Application.Ai;
using CoupleSync.Application.Budget;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Goals;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.AiChat;

/// <summary>The data message of the Assistant, and the titles of the goals it cites only by marker.</summary>
/// <param name="GoalTitles">Marker ("g1", "g2"...) to the title of the goal. The titles never leave the API.</param>
/// <param name="OtherGoalTitles">
/// Titles of the goals of the group that are not in the data (archived, completed). They do not leave either: an
/// earlier answer, sent back as history, may still cite one of them.
/// </param>
public sealed record ChatFacts(string Text, IReadOnlyDictionary<string, string> GoalTitles, IReadOnlyList<string> OtherGoalTitles);

public sealed class ChatContextService
{
    private readonly BudgetService _budgetService;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IGoalRepository _goalRepository;
    private readonly GoalProgressReader _goalProgressReader;
    private readonly IDateTimeProvider _dateTimeProvider;

    public ChatContextService(
        BudgetService budgetService,
        ITransactionRepository transactionRepository,
        IGoalRepository goalRepository,
        GoalProgressReader goalProgressReader,
        IDateTimeProvider dateTimeProvider)
    {
        _budgetService = budgetService;
        _transactionRepository = transactionRepository;
        _goalRepository = goalRepository;
        _goalProgressReader = goalProgressReader;
        _dateTimeProvider = dateTimeProvider;
    }

    /// <summary>
    /// Only the data of the group (date, budget, spending by category, goals), one fact per line, with no
    /// instruction to the model: the Assistant sends it in a message of its own, apart from the rules (which are
    /// in <see cref="AssistantChatService"/>). The title of a goal is typed by a person and may name anything, so it
    /// does not leave: each goal goes as {{g1}}, {{g2}}... and the title is put back when answering the app.
    /// </summary>
    public async Task<ChatFacts> BuildFactsAsync(Guid coupleId, CancellationToken ct)
    {
        var goalTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        var budget = await _budgetService.GetCurrentPlanAsync(coupleId, ct);
        var now = _dateTimeProvider.UtcNow;
        var since = now.AddDays(-30);
        var recentTxns = await _transactionRepository.GetRecentByCoupleAsync(coupleId, since, ct);

        // Every goal of the group: only the active ones go in the data, but the title of none of them may leave.
        var (_, goals) = await _goalRepository.GetPagedAsync(coupleId, includeArchived: true, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Data de hoje: {BrDate(now)}");

        if (budget is not null)
        {
            sb.AppendLine($"Renda bruta mensal: {BrlFormat.Format(budget.GrossIncome)}");
            sb.AppendLine($"Saldo livre (orçamento): {BrlFormat.Format(budget.BudgetGap)}");
            if (budget.Allocations.Count > 0)
            {
                sb.AppendLine("Alocações do orçamento:");
                foreach (var a in budget.Allocations)
                    sb.AppendLine($"  - {a.Category}: alocado {BrlFormat.Format(a.AllocatedAmount)}, gasto {BrlFormat.Format(a.ActualSpent)}, restante {BrlFormat.Format(a.Remaining)}");
            }
        }

        var categoryTotals = recentTxns
            .Where(t => CurrencyRules.IsBrl(t.Currency))
            .GroupBy(t => TransactionCategories.NormalizeOrOther(t.Category))
            .Select(g => new { Cat = g.Key, Total = g.Sum(t => t.Amount) })
            .ToList();

        if (categoryTotals.Count > 0)
        {
            sb.AppendLine("Gastos por categoria nos últimos 30 dias:");
            foreach (var ct_ in categoryTotals)
                sb.AppendLine($"  - {TransactionCategories.Label(ct_.Cat)}: {BrlFormat.Format(ct_.Total)}");
        }

        var activeGoals = goals.Where(g => g.Status == GoalStatus.Active).ToList();
        if (activeGoals.Count > 0)
        {
            sb.AppendLine("Metas do grupo:");
            var goalProgress = await _goalProgressReader.ReadAsync(coupleId, activeGoals, ct);
            foreach (var goal in activeGoals)
            {
                var breakdown = goalProgress[goal.Id];
                var progress = breakdown.TotalAmount;
                var percent = breakdown.ProgressPercent;
                var deadlineStr = goal.Deadline != default
                    ? BrDate(goal.Deadline)
                    : "sem prazo definido";
                var marker = $"g{goalTitles.Count + 1}";
                goalTitles[marker] = goal.Title;
                sb.AppendLine($"  - Meta {{{{{marker}}}}}: alvo {BrlFormat.Format(goal.TargetAmount)}, progresso {BrlFormat.Format(progress)} ({(long)Math.Floor(percent)}%), prazo {deadlineStr}");
            }
        }

        var otherTitles = goals.Where(g => g.Status != GoalStatus.Active).Select(g => g.Title).ToList();
        return new ChatFacts(sb.ToString(), goalTitles, otherTitles);
    }

    // Fixed dd/MM/yyyy: with a named format the "/" would follow the host culture.
    private static string BrDate(DateTime date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
