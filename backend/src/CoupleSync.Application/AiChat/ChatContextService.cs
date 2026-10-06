using CoupleSync.Domain.ValueObjects;
using System.Text;
using CoupleSync.Application.Budget;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Goals;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.AiChat;

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

    public async Task<string> BuildSystemPromptAsync(Guid coupleId, CancellationToken ct)
    {
        var budget = await _budgetService.GetCurrentPlanAsync(coupleId, ct);
        var now = _dateTimeProvider.UtcNow;
        var since = now.AddDays(-30);
        var recentTxns = await _transactionRepository.GetRecentByCoupleAsync(coupleId, since, ct);

        var (_, goals) = await _goalRepository.GetPagedAsync(coupleId, includeArchived: false, ct);

        var sb = new StringBuilder();
        sb.AppendLine("Você é um assistente financeiro do CoupleSync, um aplicativo de finanças para casais.");
        sb.AppendLine("Responda de forma clara, objetiva e sem julgamentos sobre as finanças do casal.");
        sb.AppendLine($"Data de hoje: {now:dd/MM/yyyy}");

        if (budget is not null)
        {
            sb.AppendLine($"Renda bruta mensal: R${budget.GrossIncome:N2}");
            sb.AppendLine($"Saldo livre (orçamento): R${budget.BudgetGap:N2}");
            if (budget.Allocations.Count > 0)
            {
                sb.AppendLine("Alocações do orçamento:");
                foreach (var a in budget.Allocations)
                    sb.AppendLine($"  - {a.Category}: alocado R${a.AllocatedAmount:N2}, gasto R${a.ActualSpent:N2}, restante R${a.Remaining:N2}");
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
                sb.AppendLine($"  - {TransactionCategories.Label(ct_.Cat)}: R${ct_.Total:N2}");
        }

        var activeGoals = goals.Where(g => g.Status == GoalStatus.Active).ToList();
        if (activeGoals.Count > 0)
        {
            sb.AppendLine("Metas do casal:");
            var goalProgress = await _goalProgressReader.ReadAsync(coupleId, activeGoals, ct);
            foreach (var goal in activeGoals)
            {
                var breakdown = goalProgress[goal.Id];
                var progress = breakdown.TotalAmount;
                var percent = breakdown.ProgressPercent;
                var deadlineStr = goal.Deadline != default
                    ? goal.Deadline.ToString("dd/MM/yyyy")
                    : "sem prazo definido";
                var safeTitle = goal.Title
                    .Replace("\r", string.Empty)
                    .Replace("\n", string.Empty)
                    .Trim();
                if (safeTitle.Length > 100) safeTitle = safeTitle[..100];
                sb.AppendLine($"  - {safeTitle}: alvo R${goal.TargetAmount:N2}, progresso R${progress:N2} ({Math.Floor(percent):F0}%), prazo {deadlineStr}");
            }
        }

        sb.AppendLine("IMPORTANTE: Para questões sobre investimentos, decisões legais ou fiscais, recomende que o casal consulte um profissional qualificado.");

        return sb.ToString();
    }
}
