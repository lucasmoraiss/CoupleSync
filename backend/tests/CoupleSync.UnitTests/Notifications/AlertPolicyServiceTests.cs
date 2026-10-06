using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Notifications;

public sealed class AlertPolicyServiceTests
{
    private static readonly FixedDateTimeProvider Now =
        new(new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc));

    private static Transaction BuildTransaction(
        Guid coupleId, Guid userId, decimal amount, int daysAgo = 0, string currency = "BRL", string category = "OUTROS")
    {
        return Transaction.Create(
            coupleId: coupleId,
            userId: userId,
            fingerprint: Guid.NewGuid().ToString("N"),
            bank: "NUBANK",
            amount: amount,
            currency: currency,
            eventTimestampUtc: Now.UtcNow.AddDays(-daysAgo),
            description: "Test",
            merchant: "Store",
            category: category,
            ingestEventId: Guid.NewGuid(),
            createdAtUtc: Now.UtcNow);
    }

    private static Task<IReadOnlyList<NotificationEvent>> Evaluate(
        AlertPolicyTestKit kit, Transaction tx, IReadOnlyList<Transaction>? recent = null)
        => kit.Service.EvaluatePostIngestAsync(kit.CoupleId, tx, recent ?? [], Now.UtcNow);

    // Mirrors what the callers do: the events an evaluation returns are persisted before the next one runs.
    private static async Task<IReadOnlyList<NotificationEvent>> EvaluateAndStore(
        AlertPolicyTestKit kit, Transaction tx, IReadOnlyList<Transaction>? recent = null)
    {
        var events = await Evaluate(kit, tx, recent);
        await kit.Events.AddRangeAsync(events, CancellationToken.None);
        return events;
    }

    private static void AddBudget(AlertPolicyTestKit kit, string category, decimal allocated)
    {
        var plan = BudgetPlan.Create(kit.CoupleId, "2026-04", 5000m, "BRL", Now.UtcNow);
        plan.Allocations.Add(BudgetAllocation.Create(plan.Id, category, allocated, "BRL", Now.UtcNow));
        kit.Budgets.Plans.Add(plan);
    }

    // -- Large transaction ----------------------------------------------------

    [Fact]
    public async Task LargeTransaction_AmountAbove500_CreatesAlert()
    {
        var kit = new AlertPolicyTestKit();

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Author, 600m));

        Assert.Contains(events, e => e.AlertType == "LargeTransaction");
    }

    [Fact]
    public async Task LargeTransaction_TextIsPortugueseWithReaisFormat()
    {
        var kit = new AlertPolicyTestKit();

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Author, 1234.5m));

        var alert = Assert.Single(events);
        Assert.Equal("Transação de valor alto", alert.Title);
        Assert.Equal("Uma transação de R$ 1.234,50 foi registrada.", alert.Body);
    }

    [Fact]
    public async Task LargeTransaction_GoesToEveryMemberOfTheCouple()
    {
        var kit = new AlertPolicyTestKit(memberCount: 2);

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Members[0].Id, 900m));

        Assert.Equal(2, events.Count);
        Assert.Equal(kit.Members.Select(m => m.Id).Order(), events.Select(e => e.UserId).Order());
        Assert.All(events, e => Assert.Equal(kit.CoupleId, e.CoupleId));
    }

    [Fact]
    public async Task LargeTransaction_RespectsEachMembersOwnSettings()
    {
        var kit = new AlertPolicyTestKit(memberCount: 2);
        await kit.Settings.UpsertAsync(kit.Members[1].Id, kit.CoupleId, null, largeTransaction: false, null, Now.UtcNow, CancellationToken.None);

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Members[0].Id, 900m));

        var alert = Assert.Single(events);
        Assert.Equal(kit.Members[0].Id, alert.UserId);
    }

    [Fact]
    public async Task LargeTransaction_AuthorStaysARecipientEvenWhenTheCoupleCannotBeLoaded()
    {
        var kit = new AlertPolicyTestKit();
        var strangerCouple = Guid.NewGuid();
        var author = Guid.NewGuid();

        var events = await kit.Service.EvaluatePostIngestAsync(
            strangerCouple, BuildTransaction(strangerCouple, author, 900m), [], Now.UtcNow);

        Assert.Equal(author, Assert.Single(events).UserId);
    }

    [Fact]
    public async Task LargeTransaction_InAnotherCurrency_DoesNotAlert()
    {
        var kit = new AlertPolicyTestKit();

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Author, 600m, currency: "USD"));

        Assert.DoesNotContain(events, e => e.AlertType == "LargeTransaction");
    }

    [Fact]
    public async Task LargeTransaction_AmountBelow500_DoesNotAlert()
    {
        var kit = new AlertPolicyTestKit();

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Author, 400m));

        Assert.DoesNotContain(events, e => e.AlertType == "LargeTransaction");
    }

    [Fact]
    public async Task LargeTransaction_DisabledInSettings_NoAlert()
    {
        var kit = new AlertPolicyTestKit();
        await kit.Settings.UpsertAsync(kit.Author, kit.CoupleId, null, largeTransaction: false, null, Now.UtcNow, CancellationToken.None);

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Author, 750m));

        Assert.DoesNotContain(events, e => e.AlertType == "LargeTransaction");
    }

    [Fact]
    public async Task LargeTransaction_EveryLargeTransactionAlertsAgain()
    {
        var kit = new AlertPolicyTestKit();

        await EvaluateAndStore(kit, BuildTransaction(kit.CoupleId, kit.Author, 700m));
        var second = await EvaluateAndStore(kit, BuildTransaction(kit.CoupleId, kit.Author, 800m));

        Assert.Contains(second, e => e.AlertType == "LargeTransaction");
    }

    // -- 30-day spending ------------------------------------------------------

    private static Transaction[] HighRecentSpend(AlertPolicyTestKit kit) =>
    [
        BuildTransaction(kit.CoupleId, kit.Author, 2000m, daysAgo: 5),
        BuildTransaction(kit.CoupleId, kit.Author, 1500m, daysAgo: 10),
    ];

    [Fact]
    public async Task LowBalance_RecentSpendAbove3000_CreatesPortugueseAlert()
    {
        var kit = new AlertPolicyTestKit();

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Author, 50m), HighRecentSpend(kit));

        var alert = Assert.Single(events);
        Assert.StartsWith("LowBalance", alert.AlertType);
        Assert.Equal("Gastos altos nos últimos 30 dias", alert.Title);
        Assert.Equal("Os gastos dos últimos 30 dias somam R$ 3.500,00 e passaram de R$ 3.000,00.", alert.Body);
    }

    [Fact]
    public async Task LowBalance_RecentSpendBelow3000_NoAlert()
    {
        var kit = new AlertPolicyTestKit();
        var recent = new[] { BuildTransaction(kit.CoupleId, kit.Author, 500m, daysAgo: 5) };

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Author, 50m), recent);

        Assert.DoesNotContain(events, e => e.AlertType.StartsWith("LowBalance"));
    }

    [Fact]
    public async Task LowBalance_RecentSpendInAnotherCurrency_DoesNotCount()
    {
        var kit = new AlertPolicyTestKit();
        var recent = new[]
        {
            BuildTransaction(kit.CoupleId, kit.Author, 2000m, daysAgo: 5),
            BuildTransaction(kit.CoupleId, kit.Author, 1500m, daysAgo: 10, currency: "USD"),
            BuildTransaction(kit.CoupleId, kit.Author, 1500m, daysAgo: 11, currency: "EUR"),
        };

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Author, 50m), recent);

        Assert.DoesNotContain(events, e => e.AlertType.StartsWith("LowBalance"));
    }

    [Fact]
    public async Task LowBalance_IsNotRepeatedOnEveryTransactionOfTheMonth()
    {
        var kit = new AlertPolicyTestKit(memberCount: 2);
        var recent = HighRecentSpend(kit);

        var first = await EvaluateAndStore(kit, BuildTransaction(kit.CoupleId, kit.Author, 50m), recent);
        var second = await EvaluateAndStore(kit, BuildTransaction(kit.CoupleId, kit.Author, 60m), recent);
        var third = await EvaluateAndStore(kit, BuildTransaction(kit.CoupleId, kit.Author, 70m), recent);

        Assert.Equal(2, first.Count);
        Assert.Empty(second);
        Assert.Empty(third);
    }

    [Fact]
    public async Task LowBalance_AlertsAgainInTheNextMonth()
    {
        var kit = new AlertPolicyTestKit();
        await EvaluateAndStore(kit, BuildTransaction(kit.CoupleId, kit.Author, 50m), HighRecentSpend(kit));
        var nextMonth = Now.UtcNow.AddMonths(1);
        var recentInMay = new[] { BuildTransaction(kit.CoupleId, kit.Author, 3500m, daysAgo: -29) };

        var events = await kit.Service.EvaluatePostIngestAsync(
            kit.CoupleId, BuildTransaction(kit.CoupleId, kit.Author, 50m), recentInMay, nextMonth);

        Assert.Contains(events, e => e.AlertType.StartsWith("LowBalance"));
    }

    [Fact]
    public async Task LowBalance_PlainTypeSentEarlierThisMonthCountsAsSent()
    {
        var kit = new AlertPolicyTestKit();
        await kit.Events.AddRangeAsync(
            [NotificationEvent.Create(kit.CoupleId, kit.Author, "LowBalance", "t", "b", Now.UtcNow.AddHours(-1))],
            CancellationToken.None);

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Author, 50m), HighRecentSpend(kit));

        Assert.Empty(events);
    }

    [Fact]
    public async Task LowBalance_DisabledForOneMember_OnlyTheOtherIsAlerted()
    {
        var kit = new AlertPolicyTestKit(memberCount: 2);
        await kit.Settings.UpsertAsync(kit.Members[0].Id, kit.CoupleId, lowBalance: false, null, null, Now.UtcNow, CancellationToken.None);

        var events = await Evaluate(kit, BuildTransaction(kit.CoupleId, kit.Author, 50m), HighRecentSpend(kit));

        Assert.Equal(kit.Members[1].Id, Assert.Single(events).UserId);
    }

    // -- Budget ---------------------------------------------------------------

    [Fact]
    public async Task BudgetExceeded_GoesToEveryMember_WithCategoryLabelAndReais()
    {
        var kit = new AlertPolicyTestKit(memberCount: 2);
        AddBudget(kit, "ALIMENTACAO", 1000m);
        var tx = BuildTransaction(kit.CoupleId, kit.Author, 1200.5m, category: "ALIMENTACAO");
        kit.Transactions.Transactions.Add(tx);

        var events = await Evaluate(kit, tx);

        var budgetEvents = events.Where(e => e.AlertType.StartsWith("BudgetExceeded")).ToList();
        Assert.Equal(2, budgetEvents.Count);
        Assert.All(budgetEvents, e => Assert.Equal("BudgetExceeded|ALIMENTACAO|2026-04", e.AlertType));
        Assert.Equal("Orçamento de Alimentação estourado", budgetEvents[0].Title);
        Assert.Equal("Você gastou R$ 1.200,50 de R$ 1.000,00 no orçamento de Alimentação este mês.", budgetEvents[0].Body);
    }

    [Fact]
    public async Task BudgetExceeded_IsSentOncePerCategoryPerMonth()
    {
        var kit = new AlertPolicyTestKit();
        AddBudget(kit, "ALIMENTACAO", 1000m);
        var first = BuildTransaction(kit.CoupleId, kit.Author, 1200m, category: "ALIMENTACAO");
        kit.Transactions.Transactions.Add(first);
        var firstEvents = await EvaluateAndStore(kit, first);
        var second = BuildTransaction(kit.CoupleId, kit.Author, 100m, category: "ALIMENTACAO");
        kit.Transactions.Transactions.Add(second);

        var secondEvents = await EvaluateAndStore(kit, second);

        Assert.Single(firstEvents, e => e.AlertType.StartsWith("BudgetExceeded"));
        Assert.DoesNotContain(secondEvents, e => e.AlertType.StartsWith("Budget"));
    }

    [Fact]
    public async Task Budget_WarningThenExceeded_EachThresholdIsSentOnce()
    {
        var kit = new AlertPolicyTestKit();
        AddBudget(kit, "LAZER", 1000m);
        async Task<IReadOnlyList<NotificationEvent>> Spend(decimal amount)
        {
            var tx = BuildTransaction(kit.CoupleId, kit.Author, amount, category: "LAZER");
            kit.Transactions.Transactions.Add(tx);
            var events = await EvaluateAndStore(kit, tx);
            return events.Where(e => e.AlertType.StartsWith("Budget")).ToList();
        }

        var e1 = await Spend(850m);
        var e2 = await Spend(20m);
        var e3 = await Spend(200m);
        var e4 = await Spend(10m);

        var warning = Assert.Single(e1);
        Assert.Equal("BudgetWarning|LAZER|2026-04", warning.AlertType);
        Assert.Equal("Atenção: orçamento de Lazer", warning.Title);
        Assert.Equal("Você já usou mais de 80% do orçamento de Lazer este mês (R$ 850,00 de R$ 1.000,00).", warning.Body);
        Assert.Empty(e2);
        Assert.Equal("BudgetExceeded|LAZER|2026-04", Assert.Single(e3).AlertType);
        Assert.Empty(e4);
    }

    [Theory]
    [InlineData("BudgetExceeded|Alimentação|2026-04")]
    [InlineData("BudgetExceeded|alimentacao|2026-04")]
    [InlineData("BudgetExceeded| ALIMENTAÇÃO |2026-04")]
    public async Task BudgetExceeded_AlertSentBeforeDeployUnderAnOldCategorySpelling_IsNotSentAgain(string legacyAlertType)
    {
        var kit = new AlertPolicyTestKit();
        AddBudget(kit, "ALIMENTACAO", 1000m);
        await kit.Events.AddRangeAsync(
            [NotificationEvent.Create(kit.CoupleId, kit.Author, legacyAlertType, "t", "b", Now.UtcNow.AddDays(-2))],
            CancellationToken.None);
        var tx = BuildTransaction(kit.CoupleId, kit.Author, 1200m, category: "ALIMENTACAO");
        kit.Transactions.Transactions.Add(tx);

        var events = await Evaluate(kit, tx);

        Assert.DoesNotContain(events, e => e.AlertType.StartsWith("Budget"));
    }

    [Fact]
    public async Task BudgetExceeded_LegacyAlertOfAnotherMonthOrCategory_DoesNotBlock()
    {
        var kit = new AlertPolicyTestKit();
        AddBudget(kit, "ALIMENTACAO", 1000m);
        await kit.Events.AddRangeAsync(
        [
            NotificationEvent.Create(kit.CoupleId, kit.Author, "BudgetExceeded|Alimentação|2026-03", "t", "b", Now.UtcNow.AddDays(-40)),
            NotificationEvent.Create(kit.CoupleId, kit.Author, "BudgetExceeded|Transporte|2026-04", "t", "b", Now.UtcNow.AddDays(-2)),
        ], CancellationToken.None);
        var tx = BuildTransaction(kit.CoupleId, kit.Author, 1200m, category: "ALIMENTACAO");
        kit.Transactions.Transactions.Add(tx);

        var events = await Evaluate(kit, tx);

        Assert.Contains(events, e => e.AlertType == "BudgetExceeded|ALIMENTACAO|2026-04");
    }

    [Fact]
    public async Task Budget_WithoutAllocationForTheCategory_NoAlert()
    {
        var kit = new AlertPolicyTestKit();
        AddBudget(kit, "LAZER", 100m);
        var tx = BuildTransaction(kit.CoupleId, kit.Author, 450m, category: "TRANSPORTE");
        kit.Transactions.Transactions.Add(tx);

        var events = await Evaluate(kit, tx);

        Assert.DoesNotContain(events, e => e.AlertType.StartsWith("Budget"));
    }

    // -- Statement import (B-M4): one summary instead of one alert per line -----

    [Fact]
    public async Task Import_SeveralLinesAbove500_RaisesOneSummaryAlertPerMember_WithCountAndTotal()
    {
        var kit = new AlertPolicyTestKit(memberCount: 2);
        var imported = new[]
        {
            BuildTransaction(kit.CoupleId, kit.Author, 600m),
            BuildTransaction(kit.CoupleId, kit.Author, 1234.56m),
            BuildTransaction(kit.CoupleId, kit.Author, 900m),
            BuildTransaction(kit.CoupleId, kit.Author, 20m),            // below the threshold: not counted
            BuildTransaction(kit.CoupleId, kit.Author, 700m, currency: "USD"),   // not BRL: not counted
        };

        var events = await kit.Service.EvaluatePostImportAsync(kit.CoupleId, imported, [], Now.UtcNow);

        var large = events.Where(e => e.AlertType == "LargeTransaction").ToList();
        Assert.Equal(2, large.Count);
        Assert.Equal(kit.Members.Select(m => m.Id).OrderBy(id => id), large.Select(e => e.UserId).OrderBy(id => id));
        Assert.All(large, e =>
        {
            Assert.Equal("Transações de valor alto", e.Title);
            Assert.Equal("3 transações de valor alto foram importadas do extrato, somando R$ 2.734,56.", e.Body);
        });
    }

    [Fact]
    public async Task Import_OneLineAbove500_RaisesTheSameAlertAsASingleTransaction()
    {
        var kit = new AlertPolicyTestKit();
        var imported = new[]
        {
            BuildTransaction(kit.CoupleId, kit.Author, 600m),
            BuildTransaction(kit.CoupleId, kit.Author, 20m),
        };

        var events = await kit.Service.EvaluatePostImportAsync(kit.CoupleId, imported, [], Now.UtcNow);

        var alert = Assert.Single(events, e => e.AlertType == "LargeTransaction");
        Assert.Equal("Transação de valor alto", alert.Title);
        Assert.Equal("Uma transação de R$ 600,00 foi registrada.", alert.Body);
    }

    [Fact]
    public async Task Import_NothingAbove500_OrNothingImported_RaisesNoLargeTransactionAlert()
    {
        var kit = new AlertPolicyTestKit();

        var small = await kit.Service.EvaluatePostImportAsync(
            kit.CoupleId, [BuildTransaction(kit.CoupleId, kit.Author, 20m)], [], Now.UtcNow);
        var none = await kit.Service.EvaluatePostImportAsync(kit.CoupleId, [], [], Now.UtcNow);

        Assert.DoesNotContain(small, e => e.AlertType == "LargeTransaction");
        Assert.Empty(none);
    }

    [Fact]
    public async Task Import_BudgetAlerts_AreRaisedOncePerCategoryOfTheImportedLines()
    {
        var kit = new AlertPolicyTestKit();
        var plan = BudgetPlan.Create(kit.CoupleId, "2026-04", 5000m, "BRL", Now.UtcNow);
        plan.Allocations.Add(BudgetAllocation.Create(plan.Id, "ALIMENTACAO", 100m, "BRL", Now.UtcNow));
        plan.Allocations.Add(BudgetAllocation.Create(plan.Id, "LAZER", 100m, "BRL", Now.UtcNow));
        kit.Budgets.Plans.Add(plan);
        var imported = new[]
        {
            BuildTransaction(kit.CoupleId, kit.Author, 80m, category: "ALIMENTACAO"),
            BuildTransaction(kit.CoupleId, kit.Author, 90m, category: "ALIMENTACAO"),
            BuildTransaction(kit.CoupleId, kit.Author, 150m, category: "LAZER"),
        };
        kit.Transactions.Transactions.AddRange(imported);

        var events = await kit.Service.EvaluatePostImportAsync(kit.CoupleId, imported, imported, Now.UtcNow);

        Assert.Equal(
            ["BudgetExceeded|ALIMENTACAO|2026-04", "BudgetExceeded|LAZER|2026-04"],
            events.Select(e => e.AlertType).OrderBy(t => t, StringComparer.Ordinal));
    }
}
