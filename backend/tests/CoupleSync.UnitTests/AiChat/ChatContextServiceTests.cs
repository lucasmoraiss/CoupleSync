using System.Globalization;
using CoupleSync.Application.AiChat;
using CoupleSync.Application.Budget;
using CoupleSync.Application.Budget.Queries;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.AiChat;

[Trait("Category", "AiChat")]
[Trait("Category", "ChatContext")]
public sealed class ChatContextServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 17, 10, 0, 0, DateTimeKind.Utc);

    private static (ChatContextService Service, FakeBudgetRepository BudgetRepo, FakeTransactionRepository TxRepo, FakeGoalRepository GoalRepo) Build(DateTime? now = null)
    {
        var dt = new FixedDateTimeProvider(now ?? FixedNow);
        var budgetRepo = new FakeBudgetRepository();
        var txRepo = new FakeTransactionRepository();
        var goalRepo = new FakeGoalRepository();
        var budgetService = new BudgetService(budgetRepo, txRepo, dt);
        var svc = new ChatContextService(budgetService, txRepo, goalRepo, new CoupleSync.Application.Goals.GoalProgressReader(txRepo), dt);
        return (svc, budgetRepo, txRepo, goalRepo);
    }

    [Fact]
    public async Task BuildsPromptWithBudgetData()
    {
        var (svc, budgetRepo, _, _) = Build();
        var coupleId = Guid.NewGuid();

        var plan = BudgetPlan.Create(coupleId, "2026-04", 5000m, "BRL", FixedNow);
        budgetRepo.Plans.Add(plan);

        var savedCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var prompt = (await svc.BuildFactsAsync(coupleId, CancellationToken.None)).Text;
            Assert.Contains("R$ 5.000,00", prompt);
            Assert.Contains("Renda bruta mensal", prompt);
        }
        finally
        {
            CultureInfo.CurrentCulture = savedCulture;
        }
    }

    [Fact]
    public async Task BuildsPromptWithoutBudget()
    {
        var (svc, _, _, _) = Build();
        var coupleId = Guid.NewGuid();

        var prompt = (await svc.BuildFactsAsync(coupleId, CancellationToken.None)).Text;

        Assert.Contains("Data de hoje", prompt);
        Assert.DoesNotContain("Renda bruta mensal", prompt);
    }

    [Fact]
    public async Task TheFacts_CarryNoInstructionToTheModel_TheRulesAreInTheAssistantsSystemPrompt()
    {
        var (svc, _, _, _) = Build();
        var coupleId = Guid.NewGuid();

        var prompt = (await svc.BuildFactsAsync(coupleId, CancellationToken.None)).Text;

        Assert.DoesNotContain("IMPORTANTE", prompt);
        Assert.DoesNotContain("Você é", prompt);
        Assert.DoesNotContain("profissional qualificado", prompt);
    }

    [Fact]
    public async Task IncludesRecentSpending()
    {
        var (svc, _, txRepo, _) = Build();
        var coupleId = Guid.NewGuid();

        var tx = Transaction.Create(
            coupleId: coupleId,
            userId: Guid.NewGuid(),
            fingerprint: "fp-1",
            bank: "NUBANK",
            amount: 150m,
            currency: "BRL",
            eventTimestampUtc: FixedNow.AddDays(-5),
            description: "Mercado",
            merchant: "Mercado Livre",
            category: "Alimentação",
            ingestEventId: Guid.NewGuid(),
            createdAtUtc: FixedNow);
        txRepo.Transactions.Add(tx);

        var prompt = (await svc.BuildFactsAsync(coupleId, CancellationToken.None)).Text;

        Assert.Contains("Alimentação", prompt);
        Assert.Contains("Gastos por categoria", prompt);
    }

    [Fact]
    public async Task UsesDateTimeProvider()
    {
        var customNow = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var (svc, _, _, _) = Build(customNow);
        var coupleId = Guid.NewGuid();

        var prompt = (await svc.BuildFactsAsync(coupleId, CancellationToken.None)).Text;

        Assert.Contains("15/01/2025", prompt);
    }

    // ── T-644: Goals context ───────────────────────────────────────────────

    [Fact]
    public async Task BuildsSystemPrompt_IncludesGoalsSummary_WhenActiveGoalsExist()
    {
        var (svc, _, _, goalRepo) = Build();
        var coupleId = Guid.NewGuid();

        var goal = Goal.Create(
            coupleId: coupleId,
            createdByUserId: Guid.NewGuid(),
            title: "Viagem Europa",
            description: null,
            targetAmount: 5000m,
            currency: "BRL",
            deadline: FixedNow.AddMonths(4),
            createdAtUtc: FixedNow.AddDays(-10));
        goalRepo.Goals.Add(goal);

        var savedCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var facts = await svc.BuildFactsAsync(coupleId, CancellationToken.None);
            var prompt = facts.Text;
            Assert.Contains("Metas do casal", prompt);
            Assert.Contains("R$ 5.000,00", prompt);
            // Issue #38: the title of a goal never leaves the API. The goal goes as a marker, and the title stays
            // here to be put back in the answer.
            Assert.DoesNotContain("Viagem", prompt);
            Assert.DoesNotContain("Europa", prompt);
            Assert.Contains("Meta {{g1}}: alvo R$ 5.000,00", prompt);
            Assert.Equal("Viagem Europa", Assert.Single(facts.GoalTitles).Value);
            Assert.Equal("g1", facts.GoalTitles.Keys.Single());
        }
        finally
        {
            CultureInfo.CurrentCulture = savedCulture;
        }
    }

    [Fact]
    public async Task BuildsSystemPrompt_GoalProgress_IsManualPlusLinkedTransactions()
    {
        var (svc, _, txRepo, goalRepo) = Build();
        var coupleId = Guid.NewGuid();

        var goal = Goal.Create(coupleId, Guid.NewGuid(), "Viagem", null, 1000m, "BRL", FixedNow.AddMonths(4), FixedNow.AddDays(-10));
        goal.UpdateCurrentAmount(200m, FixedNow);
        goalRepo.Goals.Add(goal);
        var tx = Transaction.Create(coupleId, Guid.NewGuid(), "fp-g", "NUBANK", 300m, "BRL", FixedNow.AddDays(-2),
            "Guardado", null, "Outros", Guid.NewGuid(), FixedNow);
        tx.LinkToGoal(goal.Id);
        txRepo.Transactions.Add(tx);

        var savedCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var prompt = (await svc.BuildFactsAsync(coupleId, CancellationToken.None)).Text;
            Assert.Contains("progresso R$ 500,00 (50%)", prompt);
        }
        finally
        {
            CultureInfo.CurrentCulture = savedCulture;
        }
    }

    [Fact]
    public async Task BuildsSystemPrompt_OmitsGoalsSection_WhenNoActiveGoals()
    {
        var (svc, _, _, _) = Build();
        var coupleId = Guid.NewGuid();

        var prompt = (await svc.BuildFactsAsync(coupleId, CancellationToken.None)).Text;

        Assert.DoesNotContain("Metas do casal", prompt);
    }

    [Fact]
    public async Task BuildsSystemPrompt_OmitsGoalsSection_WhenAllGoalsArchived()
    {
        var (svc, _, _, goalRepo) = Build();
        var coupleId = Guid.NewGuid();

        var goal = Goal.Create(
            coupleId: coupleId,
            createdByUserId: Guid.NewGuid(),
            title: "Meta arquivada",
            description: null,
            targetAmount: 1000m,
            currency: "BRL",
            deadline: FixedNow.AddMonths(1),
            createdAtUtc: FixedNow.AddDays(-30));
        goal.Archive(FixedNow);
        goalRepo.Goals.Add(goal);

        var prompt = (await svc.BuildFactsAsync(coupleId, CancellationToken.None)).Text;

        Assert.DoesNotContain("Metas do casal", prompt);
    }
}
