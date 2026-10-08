using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Application.AiFacts;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.IntegrationTests.AiChat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoupleSync.IntegrationTests.AiFacts;

/// <summary>
/// Issue #39 — "Assinaturas e recorrências" through HTTP (design 10.3), on SQLite: the list and its sections, when it
/// is calculated again, what the person corrects, the charges of a stream, the group filter and the ceiling of the
/// projection. The clock is injected (never the clock of the machine); establishments are made up.
/// </summary>
[Trait("Category", "AiFacts")]
public sealed class RecurringRoutesTests
{
    private const string Recurring = "/api/v1/ai/recurring";
    private const string Transactions = "/api/v1/transactions";

    // Noon in Brasília of 2026-10-08.
    private static readonly DateTime Now = new(2026, 10, 8, 15, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- the list

    [Fact]
    public async Task TheList_HasEachKindInItsSection_WithTheFieldsOfTheContract_AndTheTotals()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");

        foreach (var month in new[] { 8, 9, 10 })
            await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER");
        foreach (var (month, amount) in new[] { (8, 180m), (9, 230m), (10, 150m) })
            await ChargeAsync(bruno.Client, "Companhia Exemplo", amount, Day(month, 6), "MORADIA");
        await ChargeAsync(ana.Client, "LOJA 02/10", 150m, Day(9, 12), "COMPRAS");
        await ChargeAsync(ana.Client, "LOJA 03/10", 150m, Day(10, 5), "COMPRAS");
        for (var i = 0; i < 14; i++)
            await ChargeAsync(i % 2 == 0 ? ana.Client : bruno.Client, "Padaria Exemplo", 12.50m, Now.AddDays(-2 * i), "ALIMENTACAO");
        clock.UtcNow = Now.AddMinutes(1);

        var list = await ListAsync(ana.Client);

        foreach (var field in new[] { "monthlyTotal", "annualTotal", "detectedAtUtc", "subscriptions", "fixedBills", "installments", "habits", "hidden", "installmentsByMonth" })
            Assert.True(list.TryGetProperty(field, out _), $"the list should carry '{field}'");
        Assert.Equal(clock.UtcNow, list.GetProperty("detectedAtUtc").GetDateTime().ToUniversalTime());

        var subscription = Assert.Single(list.GetProperty("subscriptions").EnumerateArray());
        foreach (var field in new[]
                 {
                     "id", "name", "kind", "variableAmount", "cadence", "amount", "previousAmount", "annualCost", "occurrences", "firstSeen",
                     "lastSeen", "nextExpected", "status", "flags", "category", "person", "installment", "override",
                 })
        {
            Assert.True(subscription.TryGetProperty(field, out _), $"an item should carry '{field}'");
        }

        Assert.Equal("Streaming Exemplo", subscription.GetProperty("name").GetString());
        Assert.Equal("Subscription", subscription.GetProperty("kind").GetString());
        Assert.Equal("Monthly", subscription.GetProperty("cadence").GetString());
        Assert.Equal(39.90m, subscription.GetProperty("amount").GetDecimal());
        Assert.Equal(478.80m, subscription.GetProperty("annualCost").GetDecimal());
        Assert.Equal(3, subscription.GetProperty("occurrences").GetInt32());
        Assert.Equal("2026-08-05", subscription.GetProperty("firstSeen").GetString());
        Assert.Equal("2026-10-05", subscription.GetProperty("lastSeen").GetString());
        Assert.Equal("2026-11-05", subscription.GetProperty("nextExpected").GetString());
        Assert.Equal("Active", subscription.GetProperty("status").GetString());
        Assert.Equal("LAZER", subscription.GetProperty("category").GetString());
        Assert.Equal(JsonValueKind.Null, subscription.GetProperty("override").ValueKind);
        Assert.Equal(JsonValueKind.Null, subscription.GetProperty("installment").ValueKind);
        Assert.Equal(ana.UserId, subscription.GetProperty("person").GetProperty("userId").GetGuid());
        Assert.Equal("Ana Exemplo", subscription.GetProperty("person").GetProperty("name").GetString());

        var bill = Assert.Single(list.GetProperty("fixedBills").EnumerateArray());
        Assert.Equal("FixedBill", bill.GetProperty("kind").GetString());
        Assert.True(bill.GetProperty("variableAmount").GetBoolean());
        Assert.Equal(180m, bill.GetProperty("amount").GetDecimal());
        Assert.Equal("Bruno Exemplo", bill.GetProperty("person").GetProperty("name").GetString());

        var installment = Assert.Single(list.GetProperty("installments").EnumerateArray());
        Assert.Equal("LOJA", installment.GetProperty("name").GetString());
        Assert.Equal(3, installment.GetProperty("installment").GetProperty("number").GetInt32());
        Assert.Equal(10, installment.GetProperty("installment").GetProperty("total").GetInt32());
        Assert.Equal(1050m, installment.GetProperty("installment").GetProperty("remainingAmount").GetDecimal());
        Assert.Equal("2027-05", installment.GetProperty("installment").GetProperty("endMonth").GetString());

        var habit = Assert.Single(list.GetProperty("habits").EnumerateArray());
        Assert.Equal("Habit", habit.GetProperty("kind").GetString());
        Assert.Equal("Irregular", habit.GetProperty("cadence").GetString());
        Assert.Equal(12.50m, habit.GetProperty("amount").GetDecimal());
        Assert.Equal(14 * 12.50m * 12, habit.GetProperty("annualCost").GetDecimal());
        // Bought by both: no person.
        Assert.Equal(JsonValueKind.Null, habit.GetProperty("person").ValueKind);

        Assert.Equal(0, list.GetProperty("hidden").GetArrayLength());

        // Commitments only: the subscription, the bill (by its forecast) and the instalment. The habit is a projection.
        Assert.Equal(39.90m + 180m + 150m, list.GetProperty("monthlyTotal").GetDecimal());
        Assert.Equal(478.80m + 2160m + 1050m, list.GetProperty("annualTotal").GetDecimal());

        var byMonth = list.GetProperty("installmentsByMonth").EnumerateArray().ToList();
        Assert.Equal(12, byMonth.Count);
        Assert.Equal(("2026-10", 0m), (byMonth[0].GetProperty("month").GetString(), byMonth[0].GetProperty("amount").GetDecimal()));
        Assert.Equal(("2026-11", 150m), (byMonth[1].GetProperty("month").GetString(), byMonth[1].GetProperty("amount").GetDecimal()));
        Assert.Equal(1050m, byMonth.Sum(m => m.GetProperty("amount").GetDecimal()));
    }

    [Fact]
    public async Task AGroupWithoutEnoughHistory_GetsAnEmptyList_AndTheRouteDoesNotNeedTheAi()
    {
        // The AI is switched off on the server and nobody accepted anything: recurrences are code, not AI.
        await using var factory = new ChatWebApplicationFactory(enabled: false, configureServices: new MovingClock(Now).Register);
        var ana = await OwnerAsync(factory, "Ana Exemplo");
        await ChargeAsync(ana.Member.Client, "Streaming Exemplo", 39.90m, Day(9, 5), "LAZER");
        await ChargeAsync(ana.Member.Client, "Streaming Exemplo", 39.90m, Day(10, 5), "LAZER");

        var list = await ListAsync(ana.Member.Client);

        foreach (var section in new[] { "subscriptions", "fixedBills", "installments", "habits", "hidden" })
            Assert.Equal(0, list.GetProperty(section).GetArrayLength());
        Assert.Equal(0m, list.GetProperty("monthlyTotal").GetDecimal());
        Assert.Equal(0m, list.GetProperty("annualTotal").GetDecimal());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM recurring_streams"));
    }

    [Fact]
    public async Task OnlyBrlCounts_AndATransferToAPersonNeverBecomesAStream()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        foreach (var month in new[] { 7, 8, 9, 10 })
        {
            await ChargeAsync(ana.Client, "Servico Exemplo", 10m, Day(month, 5), "LAZER");
            await ChargeAsync(ana.Client, "Pix enviado João", 500m, Day(month, 7), "OUTROS");
            await ChargeAsync(ana.Client, "JOAO", 300m, Day(month, 8), "OUTROS", description: "TED");
            await ChargeAsync(ana.Client, "MARIA S SILVA", 200m, Day(month, 9), "OUTROS");
        }

        // The app only creates BRL; a row in another currency (older data) is written straight in.
        factory.Execute("UPDATE transactions SET currency = 'USD' WHERE merchant = 'Servico Exemplo'");
        Assert.Equal(4, factory.Scalar<long>("SELECT count(*) FROM transactions WHERE currency = 'USD'"));

        clock.UtcNow = Now.AddMinutes(1);
        var list = await ListAsync(ana.Client);

        foreach (var section in new[] { "subscriptions", "fixedBills", "installments", "habits", "hidden" })
            Assert.Equal(0, list.GetProperty(section).GetArrayLength());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM recurring_streams"));
    }

    // ---------------------------------------------------------------- when it is calculated again

    [Fact]
    public async Task ASecondRequestWithinSixHours_WithNoNewTransaction_DoesNotCalculateAgain_AndANewTransactionOrSixHoursDo()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        foreach (var month in new[] { 8, 9, 10 })
            await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER");

        clock.UtcNow = Now.AddMinutes(1);
        var first = await ListAsync(ana.Client);
        Assert.Equal(Now.AddMinutes(1), DetectedAt(first));
        Assert.Equal(Stamp(Now.AddMinutes(1)), Stamp(factory.Scalar<DateTime>("SELECT detected_at_utc FROM recurring_streams")));

        // 5 h 58 min later, nothing new: the stored streams are answered as they are.
        clock.UtcNow = Now.AddHours(5).AddMinutes(59);
        var second = await ListAsync(ana.Client);
        Assert.Equal(Now.AddMinutes(1), DetectedAt(second));
        Assert.Equal(Stamp(Now.AddMinutes(1)), Stamp(factory.Scalar<DateTime>("SELECT detected_at_utc FROM recurring_streams")));
        Assert.Equal(Stamp(Now.AddMinutes(1)), Stamp(factory.Scalar<DateTime>("SELECT updated_at_utc FROM recurring_streams")));

        // A transaction comes in: the next request calculates again, even though 6 hours have not passed.
        await ChargeAsync(ana.Client, "Outra Loja Exemplo", 10m, clock.UtcNow, "COMPRAS");
        clock.UtcNow = Now.AddHours(5).AddMinutes(59).AddSeconds(30);
        var third = await ListAsync(ana.Client);
        Assert.Equal(clock.UtcNow, DetectedAt(third));
        Assert.Equal(Stamp(clock.UtcNow), Stamp(factory.Scalar<DateTime>("SELECT detected_at_utc FROM recurring_streams")));
        var recalculatedAt = clock.UtcNow;

        // Nothing new again.
        clock.UtcNow = recalculatedAt.AddHours(3);
        Assert.Equal(recalculatedAt, DetectedAt(await ListAsync(ana.Client)));

        // Six hours after the last calculation: again.
        clock.UtcNow = recalculatedAt.AddHours(6);
        Assert.Equal(clock.UtcNow, DetectedAt(await ListAsync(ana.Client)));
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM recurring_streams"));
    }

    /// <summary>What is kept in memory is a shortcut: a host that just started decides by what is stored.</summary>
    [Fact]
    public async Task AfterARestart_TheStoredCalculationStillCounts_AndANewTransactionStillForcesANewOne()
    {
        var clock = new MovingClock(Now);
        var database = $"couplesync-recurring-restart-{Guid.NewGuid():N}";
        await using var first = new ChatWebApplicationFactory(enabled: true, databaseName: database, configureServices: clock.Register);
        var ana = (await OwnerAsync(first, "Ana Exemplo")).Member;
        foreach (var month in new[] { 8, 9, 10 })
            await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER");
        clock.UtcNow = Now.AddMinutes(1);
        Assert.Equal(Now.AddMinutes(1), DetectedAt(await ListAsync(ana.Client)));

        await using var second = new ChatWebApplicationFactory(enabled: true, databaseName: database, configureServices: clock.Register);
        using var client = second.CreateClient();
        client.DefaultRequestHeaders.Authorization = ana.Client.DefaultRequestHeaders.Authorization;

        clock.UtcNow = Now.AddHours(2);
        Assert.Equal(Now.AddMinutes(1), DetectedAt(await ListAsync(client)));

        await ChargeAsync(client, "Outra Loja Exemplo", 10m, clock.UtcNow, "COMPRAS");
        clock.UtcNow = Now.AddHours(2).AddMinutes(1);
        Assert.Equal(clock.UtcNow, DetectedAt(await ListAsync(client)));
    }

    [Fact]
    public async Task DeletingATransactionOfAStream_Works_AndTheNextRequestCalculatesAgain()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        var ids = new List<Guid>();
        foreach (var month in new[] { 8, 9, 10 })
            ids.Add(await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER"));
        clock.UtcNow = Now.AddMinutes(1);
        Assert.Equal(1, (await ListAsync(ana.Client)).GetProperty("subscriptions").GetArrayLength());
        Assert.Equal(3, factory.Scalar<long>("SELECT count(*) FROM recurring_stream_items"));

        var deleted = await ana.Client.DeleteAsync($"{Transactions}/{ids[0]}");
        Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        Assert.Equal(2, factory.Scalar<long>("SELECT count(*) FROM recurring_stream_items"));

        // Two charges are not a recurrence any more, and the list says so at once (not six hours later).
        clock.UtcNow = Now.AddMinutes(2);
        var list = await ListAsync(ana.Client);
        Assert.Equal(0, list.GetProperty("subscriptions").GetArrayLength());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM recurring_streams"));
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM recurring_stream_items"));
    }

    // ---------------------------------------------------------------- what the person corrects

    [Fact]
    public async Task NotRecurring_SurvivesTheRecalculation_AndTakesTheItemOutOfTheListAndOfTheTotals()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        foreach (var month in new[] { 8, 9, 10 })
            await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER");
        clock.UtcNow = Now.AddMinutes(1);
        var id = (await ListAsync(ana.Client)).GetProperty("subscriptions")[0].GetProperty("id").GetGuid();

        var patched = await PatchAsync(ana.Client, id, "NotRecurring");
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var item = await patched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal("NotRecurring", item.GetProperty("override").GetString());

        // A new transaction and seven hours force a recalculation: the correction is still there.
        await ChargeAsync(ana.Client, "Outra Loja Exemplo", 10m, clock.UtcNow, "COMPRAS");
        clock.UtcNow = Now.AddHours(7);
        var list = await ListAsync(ana.Client);

        Assert.Equal(clock.UtcNow, DetectedAt(list));
        Assert.Equal(0, list.GetProperty("subscriptions").GetArrayLength());
        var hidden = Assert.Single(list.GetProperty("hidden").EnumerateArray());
        Assert.Equal(id, hidden.GetProperty("id").GetGuid());
        Assert.Equal("NotRecurring", hidden.GetProperty("override").GetString());
        Assert.Equal(0m, list.GetProperty("monthlyTotal").GetDecimal());
        Assert.Equal(0m, list.GetProperty("annualTotal").GetDecimal());
        Assert.Equal("NotRecurring", factory.Scalar<string>("SELECT user_override FROM recurring_streams"));
        Assert.Equal(1, factory.Scalar<long>($"SELECT count(*) FROM recurring_streams WHERE upper(override_by_user_id) = '{ana.UserId.ToString().ToUpperInvariant()}'"));

        // Taking it back (null) puts it in the list again.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(ana.Client, id, null)).StatusCode);
        var back = await ListAsync(ana.Client);
        Assert.Equal(id, Assert.Single(back.GetProperty("subscriptions").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(0, back.GetProperty("hidden").GetArrayLength());
        Assert.Equal(39.90m, back.GetProperty("monthlyTotal").GetDecimal());
    }

    [Fact]
    public async Task Cancelled_LeavesTheList_AndComesBackMarked_WhenItIsChargedAgain()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        foreach (var month in new[] { 8, 9, 10 })
            await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER");
        clock.UtcNow = Now.AddMinutes(1);
        var id = (await ListAsync(ana.Client)).GetProperty("subscriptions")[0].GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(ana.Client, id, "Cancelled")).StatusCode);

        // A recalculation with no new charge of it: still out of the list.
        await ChargeAsync(ana.Client, "Outra Loja Exemplo", 10m, clock.UtcNow, "COMPRAS");
        clock.UtcNow = Now.AddHours(1);
        var cancelled = await ListAsync(ana.Client);
        Assert.Equal(0, cancelled.GetProperty("subscriptions").GetArrayLength());
        var hidden = Assert.Single(cancelled.GetProperty("hidden").EnumerateArray());
        Assert.Equal("Cancelled", hidden.GetProperty("override").GetString());
        // Only "New" (its first charge is 64 days old): nothing says it was charged again.
        Assert.Equal(["New"], hidden.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(0m, cancelled.GetProperty("monthlyTotal").GetDecimal());

        // The next month it is charged again.
        clock.UtcNow = new DateTime(2026, 11, 5, 15, 0, 0, DateTimeKind.Utc);
        await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, clock.UtcNow, "LAZER");
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var charged = await ListAsync(ana.Client);

        var item = Assert.Single(charged.GetProperty("subscriptions").EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal("Cancelled", item.GetProperty("override").GetString());
        Assert.Contains("ChargedAfterCancel", item.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(0, charged.GetProperty("hidden").GetArrayLength());
        Assert.Equal(39.90m, charged.GetProperty("monthlyTotal").GetDecimal());
    }

    [Fact]
    public async Task SubscriptionAndFixedBill_MoveTheItemToTheOtherSection_AndSurviveTheRecalculation()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        foreach (var month in new[] { 8, 9, 10 })
            await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER");
        clock.UtcNow = Now.AddMinutes(1);
        var id = (await ListAsync(ana.Client)).GetProperty("subscriptions")[0].GetProperty("id").GetGuid();

        var patched = await (await PatchAsync(ana.Client, id, "FixedBill")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FixedBill", patched.GetProperty("kind").GetString());

        clock.UtcNow = Now.AddHours(7);
        var list = await ListAsync(ana.Client);
        Assert.Equal(0, list.GetProperty("subscriptions").GetArrayLength());
        var bill = Assert.Single(list.GetProperty("fixedBills").EnumerateArray());
        Assert.Equal(id, bill.GetProperty("id").GetGuid());
        Assert.Equal("FixedBill", bill.GetProperty("override").GetString());
        Assert.Equal(39.90m, list.GetProperty("monthlyTotal").GetDecimal());

        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(ana.Client, id, "Subscription")).StatusCode);
        Assert.Equal(id, Assert.Single((await ListAsync(ana.Client)).GetProperty("subscriptions").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("x")]
    [InlineData("")]
    [InlineData("notrecurring")]
    [InlineData("Installment")]
    public async Task AnUnknownOverride_Is400InvalidOverride_AndNothingChanges(string value)
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        foreach (var month in new[] { 8, 9, 10 })
            await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER");
        clock.UtcNow = Now.AddMinutes(1);
        var id = (await ListAsync(ana.Client)).GetProperty("subscriptions")[0].GetProperty("id").GetGuid();

        var response = await PatchAsync(ana.Client, id, value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ErrorOf(response);
        Assert.Equal("INVALID_OVERRIDE", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM recurring_streams WHERE user_override IS NOT NULL"));
    }

    // ---------------------------------------------------------------- the charges of a stream

    [Fact]
    public async Task TheChargesOfAStream_AreItsTransactions_NewestFirst_WithoutTheDescription()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        var ids = new List<Guid>();
        foreach (var month in new[] { 8, 9, 10 })
            ids.Add(await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER", description: "anotacao-particular-xyz"));
        await ChargeAsync(ana.Client, "Streaming Exemplo", 250m, Day(9, 20), "LAZER");
        clock.UtcNow = Now.AddMinutes(1);
        var list = await ListAsync(ana.Client);
        var id = list.GetProperty("subscriptions")[0].GetProperty("id").GetGuid();

        var response = await ana.Client.GetAsync($"{Recurring}/{id}/transactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var charges = JsonDocument.Parse(text).RootElement.GetProperty("transactions").EnumerateArray().ToList();
        Assert.Equal(Enumerable.Reverse(ids), charges.Select(c => c.GetProperty("transactionId").GetGuid()));
        Assert.All(charges, c => Assert.Equal(39.90m, c.GetProperty("amount").GetDecimal()));
        Assert.All(charges, c => Assert.Equal("Streaming Exemplo", c.GetProperty("merchant").GetString()));
        Assert.Equal(Day(10, 5), charges[0].GetProperty("date").GetDateTime().ToUniversalTime());
        // The description is only read on the server.
        Assert.DoesNotContain("anotacao-particular-xyz", text);
        Assert.DoesNotContain("anotacao-particular-xyz", list.GetRawText());
    }

    // ---------------------------------------------------------------- one group never sees another

    [Fact]
    public async Task TheStreamsOfOneGroup_DoNotShowUpForAnother_AndItsIdsAre404RecurrenceNotFound()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        var carla = (await OwnerAsync(factory, "Carla Exemplo")).Member;
        foreach (var month in new[] { 8, 9, 10 })
            await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER");
        clock.UtcNow = Now.AddMinutes(1);
        var id = (await ListAsync(ana.Client)).GetProperty("subscriptions")[0].GetProperty("id").GetGuid();

        var other = await ListAsync(carla.Client);
        foreach (var section in new[] { "subscriptions", "fixedBills", "installments", "habits", "hidden" })
            Assert.Equal(0, other.GetProperty(section).GetArrayLength());
        Assert.Equal(0m, other.GetProperty("monthlyTotal").GetDecimal());

        foreach (var response in new[]
                 {
                     await carla.Client.GetAsync($"{Recurring}/{id}/transactions"),
                     await PatchAsync(carla.Client, id, "NotRecurring"),
                     await ana.Client.GetAsync($"{Recurring}/{Guid.NewGuid()}/transactions"),
                     await PatchAsync(ana.Client, Guid.NewGuid(), "NotRecurring"),
                 })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var error = await ErrorOf(response);
            Assert.Equal("RECURRENCE_NOT_FOUND", error.Code);
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
        }

        // Nothing of the other group was changed, and its own list is still there.
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM recurring_streams WHERE user_override IS NOT NULL"));
        Assert.Equal(1, (await ListAsync(ana.Client)).GetProperty("subscriptions").GetArrayLength());
    }

    [Theory]
    [InlineData("GET", "/api/v1/ai/recurring")]
    [InlineData("GET", "/api/v1/ai/recurring/7f1b0c1e-0000-4000-8000-000000000001/transactions")]
    [InlineData("PATCH", "/api/v1/ai/recurring/7f1b0c1e-0000-4000-8000-000000000001")]
    public async Task EveryRoute_Needs_ASession_AndAGroup(string method, string path)
    {
        await using var factory = NewFactory(new MovingClock(Now));
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, method, path)).StatusCode);

        var loner = await RegisterAsync(factory, "Sem Grupo");
        var response = await SendAsync(loner.Client, method, path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("COUPLE_REQUIRED", (await ErrorOf(response)).Code);
    }

    // ---------------------------------------------------------------- the ceiling of the projection

    [Fact]
    public async Task AGroupWithMoreThan20000RowsIn13Months_ReadsOnlyTheCeiling_TheMostRecentOnes_WithoutError()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        // One real transaction gives the rows to copy (group, person, ingest event).
        await ChargeAsync(ana.Client, "Semente Exemplo", 1m, Now, "OUTROS");
        // 20,050 more, one every 20 minutes going back from now (about 278 days): each with its own establishment.
        factory.Execute(
            """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 20050)
            INSERT INTO transactions (id, couple_id, user_id, fingerprint, bank, amount, currency, event_timestamp_utc, description, merchant, category, ingest_event_id, goal_id, source, created_at_utc)
            SELECT printf('%08X-0000-4000-8000-%012X', i, i), t.couple_id, t.user_id, 'cap:' || i, 'MANUAL', 10 + (i % 7), 'BRL',
                   strftime('%Y-%m-%d %H:%M:%S', '2026-10-08 14:00:00', '-' || (i * 20) || ' minutes'), NULL, 'Loja Exemplo ' || i, 'COMPRAS', t.ingest_event_id, NULL, t.source,
                   '2026-10-08 14:00:00'
            FROM n, (SELECT * FROM transactions LIMIT 1) AS t
            """);
        Assert.Equal(20051, factory.Scalar<long>("SELECT count(*) FROM transactions"));

        using (var scope = factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRecurringStreamRepository>();
            var options = new RecurrenceOptions();
            var rows = await repository.ReadProjectionAsync(ana.CoupleId, Now.AddMonths(-13), options.ProjectionRowCap, CancellationToken.None);

            Assert.Equal(20_000, options.ProjectionRowCap);
            Assert.Equal(20_000, rows.Count);
            // The 51 that were left out are the oldest ones.
            var oldestRead = rows.Min(r => r.TimestampUtc);
            Assert.Equal(new DateTime(2026, 10, 8, 14, 0, 0, DateTimeKind.Utc).AddMinutes(-19_999 * 20), oldestRead);
            Assert.Contains(rows, r => r.Merchant == "Semente Exemplo");
        }

        clock.UtcNow = Now.AddMinutes(1);
        var response = await ana.Client.GetAsync(Recurring);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, list.GetProperty("subscriptions").GetArrayLength());
    }

    // ---------------------------------------------------------------- review round 1

    /// <summary>
    /// I1 — a yearly charge has two charges a year apart; a month after the renewal the first one leaves the 13
    /// months the detector reads. The item stays, with its numbers, until the next renewal is late.
    /// </summary>
    [Fact]
    public async Task AYearlyCharge_StaysInTheListAndInTheTotals_UntilTheNextRenewalIsLate()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        await ChargeAsync(ana.Client, "Seguro Exemplo", 1200m, new DateTime(2025, 9, 22, 15, 0, 0, DateTimeKind.Utc), "TRANSPORTE");
        await ChargeAsync(ana.Client, "Seguro Exemplo", 1200m, Day(9, 22), "TRANSPORTE");
        clock.UtcNow = Now.AddMinutes(1);
        var first = Assert.Single((await ListAsync(ana.Client)).GetProperty("fixedBills").EnumerateArray());
        var id = first.GetProperty("id").GetGuid();
        Assert.Equal("Yearly", first.GetProperty("cadence").GetString());

        // 31, 60, 160, 300 and 395 days after the renewal of 2026-09-22.
        foreach (var day in new[] { new DateTime(2026, 10, 23), new DateTime(2026, 11, 21), new DateTime(2027, 3, 1), new DateTime(2027, 7, 19), new DateTime(2027, 10, 22) })
        {
            clock.UtcNow = DateTime.SpecifyKind(day.AddHours(15), DateTimeKind.Utc);
            var list = await ListAsync(ana.Client);

            Assert.True(list.GetProperty("fixedBills").GetArrayLength() == 1, $"the yearly item is gone on {day:yyyy-MM-dd}");
            var item = list.GetProperty("fixedBills")[0];
            Assert.Equal(id, item.GetProperty("id").GetGuid());
            Assert.Equal("Active", item.GetProperty("status").GetString());
            Assert.Equal(2, item.GetProperty("occurrences").GetInt32());
            Assert.Equal("2026-09-22", item.GetProperty("lastSeen").GetString());
            Assert.Equal("2027-09-22", item.GetProperty("nextExpected").GetString());
            Assert.Equal(0, list.GetProperty("hidden").GetArrayLength());
            Assert.Equal(100m, list.GetProperty("monthlyTotal").GetDecimal());
            Assert.Equal(1200m, list.GetProperty("annualTotal").GetDecimal());
            // The list says when it was calculated: the kept item does not make every request calculate again.
            Assert.Equal(clock.UtcNow, DetectedAt(list));
            Assert.Equal(Stamp(clock.UtcNow), Stamp(factory.Scalar<DateTime>("SELECT detected_at_utc FROM recurring_streams")));
        }

        // The renewal of 2027 never came: 396 days after the last charge the item leaves.
        clock.UtcNow = new DateTime(2027, 10, 23, 15, 0, 0, DateTimeKind.Utc);
        var late = await ListAsync(ana.Client);
        Assert.Equal(0, late.GetProperty("fixedBills").GetArrayLength());
        Assert.Equal(0, late.GetProperty("hidden").GetArrayLength());
        Assert.Equal(0m, late.GetProperty("monthlyTotal").GetDecimal());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM recurring_streams"));
    }

    [Fact]
    public async Task AYearlyChargeThePersonCorrected_IsNotSentToHiddenAsStopped_AndIsTheSameItemAfterTheRenewal()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        await ChargeAsync(ana.Client, "Anuidade Exemplo", 120m, new DateTime(2025, 9, 22, 15, 0, 0, DateTimeKind.Utc), "LAZER");
        await ChargeAsync(ana.Client, "Anuidade Exemplo", 120m, Day(9, 22), "LAZER");
        clock.UtcNow = Now.AddMinutes(1);
        var id = Assert.Single((await ListAsync(ana.Client)).GetProperty("subscriptions").EnumerateArray()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(ana.Client, id, "FixedBill")).StatusCode);

        clock.UtcNow = new DateTime(2027, 2, 10, 15, 0, 0, DateTimeKind.Utc);
        var middle = await ListAsync(ana.Client);
        var kept = Assert.Single(middle.GetProperty("fixedBills").EnumerateArray());
        Assert.Equal(id, kept.GetProperty("id").GetGuid());
        Assert.Equal("Active", kept.GetProperty("status").GetString());
        Assert.Equal(0, middle.GetProperty("hidden").GetArrayLength());
        Assert.Equal(10m, middle.GetProperty("monthlyTotal").GetDecimal());

        // The renewal comes: the same item (and its correction) goes on.
        clock.UtcNow = new DateTime(2027, 9, 22, 15, 0, 0, DateTimeKind.Utc);
        await ChargeAsync(ana.Client, "Anuidade Exemplo", 120m, clock.UtcNow, "LAZER");
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var renewed = Assert.Single((await ListAsync(ana.Client)).GetProperty("fixedBills").EnumerateArray());
        Assert.Equal(id, renewed.GetProperty("id").GetGuid());
        Assert.Equal("2027-09-22", renewed.GetProperty("lastSeen").GetString());
        Assert.Equal("FixedBill", renewed.GetProperty("override").GetString());
    }

    [Fact]
    public async Task AYearlyChargeWhoseLastChargeWasDeleted_IsNotKept()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        await ChargeAsync(ana.Client, "Seguro Exemplo", 1200m, new DateTime(2025, 9, 22, 15, 0, 0, DateTimeKind.Utc), "TRANSPORTE");
        var last = await ChargeAsync(ana.Client, "Seguro Exemplo", 1200m, Day(9, 22), "TRANSPORTE");
        clock.UtcNow = Now.AddMinutes(1);
        Assert.Equal(1, (await ListAsync(ana.Client)).GetProperty("fixedBills").GetArrayLength());

        clock.UtcNow = new DateTime(2026, 11, 21, 15, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, (await ListAsync(ana.Client)).GetProperty("fixedBills").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"{Transactions}/{last}")).StatusCode);

        var list = await ListAsync(ana.Client);
        Assert.Equal(0, list.GetProperty("fixedBills").GetArrayLength());
        Assert.Equal(0m, list.GetProperty("monthlyTotal").GetDecimal());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM recurring_streams"));
    }

    /// <summary>
    /// I5 — the screen says of "Cancelei": "if it is charged again, it comes back with a notice". The charge that
    /// comes back more than two months later does not continue the series, and still has to bring the item back.
    /// </summary>
    [Fact]
    public async Task Cancelled_ComesBackToTheListAndToTheTotal_AlsoWhenTheChargeReturnsMonthsLater()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        foreach (var month in new[] { 8, 9, 10 })
            await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER");
        clock.UtcNow = Now.AddMinutes(1);
        var id = (await ListAsync(ana.Client)).GetProperty("subscriptions")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(ana.Client, id, "Cancelled")).StatusCode);

        // 75 days without a charge: it stopped, and it is hidden because the person cancelled it.
        clock.UtcNow = new DateTime(2026, 12, 19, 15, 0, 0, DateTimeKind.Utc);
        var quiet = await ListAsync(ana.Client);
        Assert.Equal(0, quiet.GetProperty("subscriptions").GetArrayLength());
        Assert.Equal("Stopped", Assert.Single(quiet.GetProperty("hidden").EnumerateArray()).GetProperty("status").GetString());

        // 76 days after the last charge it is charged again.
        clock.UtcNow = new DateTime(2026, 12, 20, 15, 0, 0, DateTimeKind.Utc);
        await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, clock.UtcNow, "LAZER");
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var charged = await ListAsync(ana.Client);

        var item = Assert.Single(charged.GetProperty("subscriptions").EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Contains("ChargedAfterCancel", item.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(0, charged.GetProperty("hidden").GetArrayLength());
        Assert.Equal(39.90m, charged.GetProperty("monthlyTotal").GetDecimal());
        Assert.Equal(478.80m, charged.GetProperty("annualTotal").GetDecimal());

        // "Cancelei" again: hidden again, without the notice, until a charge after this moment.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(ana.Client, id, "Cancelled")).StatusCode);
        clock.UtcNow = clock.UtcNow.AddHours(7);
        var again = await ListAsync(ana.Client);
        Assert.Equal(0, again.GetProperty("subscriptions").GetArrayLength());
        Assert.Empty(Assert.Single(again.GetProperty("hidden").EnumerateArray()).GetProperty("flags").EnumerateArray());
        Assert.Equal(0m, again.GetProperty("monthlyTotal").GetDecimal());
    }

    /// <summary>
    /// Decision 1 (I4) — a statement line has no merchant: its description is the establishment. The row says where
    /// the name came from, so that a name read from a description never goes to a provider (phase 4).
    /// </summary>
    [Fact]
    public async Task WithoutAMerchant_TheDescriptionIsTheName_ForTheGroupItself_AndTheRowSaysWhereTheNameCameFrom()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        foreach (var month in new[] { 8, 9, 10 })
        {
            await ChargeAsync(ana.Client, null, 59.90m, Day(month, 5), "LAZER", description: "Clube Exemplo mensalidade");
            await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 6), "LAZER", description: "anotacao-particular-xyz");
            // The same shop, once with a merchant and twice only with the description.
            await ChargeAsync(ana.Client, month == 9 ? "Revista Exemplo" : null, 19.90m, Day(month, 7), "LAZER", description: "Revista Exemplo");
            // A document in the description: no stream at all.
            await ChargeAsync(ana.Client, null, 300m, Day(month, 8), "OUTROS", description: "Aula Exemplo 123.456.789-09");
        }

        clock.UtcNow = Now.AddMinutes(1);
        var list = await ListAsync(ana.Client);

        var names = list.GetProperty("subscriptions").EnumerateArray().Select(s => s.GetProperty("name").GetString()).Order().ToList();
        Assert.Equal(["Clube Exemplo mensalidade", "Revista Exemplo", "Streaming Exemplo"], names);
        Assert.Equal(0, list.GetProperty("fixedBills").GetArrayLength());
        Assert.DoesNotContain("123.456.789-09", list.GetRawText());
        Assert.DoesNotContain("anotacao-particular-xyz", list.GetRawText());
        Assert.Equal(0, factory.Scalar<long>("SELECT count(*) FROM recurring_streams WHERE display_name LIKE '%Aula%' OR merchant_key LIKE '%aula%'"));

        Assert.Equal("description", factory.Scalar<string>("SELECT name_source FROM recurring_streams WHERE merchant_key = 'clube exemplo mensalidade'"));
        Assert.Equal("merchant", factory.Scalar<string>("SELECT name_source FROM recurring_streams WHERE merchant_key = 'streaming exemplo'"));
        // One charge whose text came from a description is enough.
        Assert.Equal("description", factory.Scalar<string>("SELECT name_source FROM recurring_streams WHERE merchant_key = 'revista exemplo'"));

        // The charges show what the list of transactions already shows to the group.
        var clube = list.GetProperty("subscriptions").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Clube Exemplo mensalidade");
        var charges = await ana.Client.GetFromJsonAsync<JsonElement>($"{Recurring}/{clube.GetProperty("id").GetGuid()}/transactions");
        Assert.Equal(3, charges.GetProperty("transactions").GetArrayLength());
        Assert.All(charges.GetProperty("transactions").EnumerateArray(), c => Assert.Equal("Clube Exemplo mensalidade", c.GetProperty("merchant").GetString()));
    }

    /// <summary>Decision 6 — who edits an old transaction sees the effect in the next request, like who deletes one.</summary>
    [Fact]
    public async Task EditingATransaction_ForcesTheRecalculation()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var ana = (await OwnerAsync(factory, "Ana Exemplo")).Member;
        var ids = new List<Guid>();
        foreach (var month in new[] { 8, 9, 10 })
            ids.Add(await ChargeAsync(ana.Client, "Streaming Exemplo", 39.90m, Day(month, 5), "LAZER"));
        clock.UtcNow = Now.AddMinutes(1);
        var before = Assert.Single((await ListAsync(ana.Client)).GetProperty("subscriptions").EnumerateArray());
        Assert.Equal(39.90m, before.GetProperty("lastAmount").GetDecimal());

        // The amount (PATCH /transactions/{id}).
        clock.UtcNow = Now.AddMinutes(2);
        var patched = await ana.Client.PatchAsync($"{Transactions}/{ids[2]}", JsonContent.Create(new { Amount = 41.90m }));
        Assert.True(patched.StatusCode == HttpStatusCode.OK, await patched.Content.ReadAsStringAsync());
        clock.UtcNow = Now.AddMinutes(3);
        var afterAmount = await ListAsync(ana.Client);
        Assert.Equal(41.90m, Assert.Single(afterAmount.GetProperty("subscriptions").EnumerateArray()).GetProperty("lastAmount").GetDecimal());
        Assert.Equal(clock.UtcNow, DetectedAt(afterAmount));

        // No edit: no recalculation.
        clock.UtcNow = Now.AddMinutes(4);
        Assert.Equal(Now.AddMinutes(3), DetectedAt(await ListAsync(ana.Client)));

        // The category (PATCH /transactions/{id}/category): the subscription becomes a fixed bill.
        foreach (var transactionId in ids)
        {
            var recategorized = await ana.Client.PatchAsync($"{Transactions}/{transactionId}/category", JsonContent.Create(new { Category = "MORADIA" }));
            Assert.True(recategorized.StatusCode == HttpStatusCode.OK, await recategorized.Content.ReadAsStringAsync());
        }

        clock.UtcNow = Now.AddMinutes(5);
        var afterCategory = await ListAsync(ana.Client);
        Assert.Equal(0, afterCategory.GetProperty("subscriptions").GetArrayLength());
        Assert.Equal("MORADIA", Assert.Single(afterCategory.GetProperty("fixedBills").EnumerateArray()).GetProperty("category").GetString());

        // The establishment (PATCH /transactions/{id}): the series loses a charge and is not a series any more.
        var renamed = await ana.Client.PatchAsync($"{Transactions}/{ids[0]}", JsonContent.Create(new { Merchant = "Outra Loja Exemplo" }));
        Assert.True(renamed.StatusCode == HttpStatusCode.OK, await renamed.Content.ReadAsStringAsync());
        clock.UtcNow = Now.AddMinutes(6);
        var afterName = await ListAsync(ana.Client);
        Assert.Equal(0, afterName.GetProperty("fixedBills").GetArrayLength());
        Assert.Equal(0, afterName.GetProperty("subscriptions").GetArrayLength());
    }

    /// <summary>
    /// I2 — each person pays the same plan of the same service. Two items, each with its person; what one of them
    /// says about theirs stays on theirs, and the charge of the other is not "charged again after cancelled".
    /// </summary>
    [Fact]
    public async Task TheSamePlanOfEachPerson_IsTwoItems_AndTheCorrectionOfOneDoesNotMoveToTheOther()
    {
        var clock = new MovingClock(Now);
        await using var factory = NewFactory(clock);
        var (ana, bruno) = await TwoMembersAsync(factory, "Ana Exemplo", "Bruno Exemplo");
        foreach (var month in new[] { 6, 7, 8, 9 })
        {
            await ChargeAsync(ana.Client, "Musica Exemplo", 21.90m, Day(month, 3), "LAZER");
            await ChargeAsync(bruno.Client, "Musica Exemplo", 21.90m, Day(month, 17), "LAZER");
        }

        clock.UtcNow = Now.AddMinutes(1);
        var list = await ListAsync(ana.Client);

        var items = list.GetProperty("subscriptions").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(["Ana Exemplo", "Bruno Exemplo"], items.Select(i => i.GetProperty("person").GetProperty("name").GetString()).Order());
        Assert.Equal(43.80m, list.GetProperty("monthlyTotal").GetDecimal());
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM recurring_streams WHERE merchant_key = 'musica exemplo'"));
        Assert.Equal(1, factory.Scalar<long>("SELECT count(*) FROM recurring_streams WHERE merchant_key = 'musica exemplo~2'"));
        var ofAna = items.Single(i => i.GetProperty("person").GetProperty("userId").GetGuid() == ana.UserId).GetProperty("id").GetGuid();
        var ofBruno = items.Single(i => i.GetProperty("person").GetProperty("userId").GetGuid() == bruno.UserId).GetProperty("id").GetGuid();

        // Ana cancels hers. Bruno goes on paying his.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(ana.Client, ofAna, "Cancelled")).StatusCode);
        clock.UtcNow = Day(10, 17);
        await ChargeAsync(bruno.Client, "Musica Exemplo", 21.90m, clock.UtcNow, "LAZER");
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var later = await ListAsync(bruno.Client);

        var active = Assert.Single(later.GetProperty("subscriptions").EnumerateArray());
        Assert.Equal(ofBruno, active.GetProperty("id").GetGuid());
        Assert.Equal(5, active.GetProperty("occurrences").GetInt32());
        Assert.Equal(JsonValueKind.Null, active.GetProperty("override").ValueKind);
        var hidden = Assert.Single(later.GetProperty("hidden").EnumerateArray());
        Assert.Equal(ofAna, hidden.GetProperty("id").GetGuid());
        Assert.Equal("Cancelled", hidden.GetProperty("override").GetString());
        Assert.Empty(hidden.GetProperty("flags").EnumerateArray());
        Assert.Equal(21.90m, later.GetProperty("monthlyTotal").GetDecimal());
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Member(HttpClient Client, Guid UserId, Guid CoupleId);

    private sealed record ErrorDto(string Code, string Message);

    private static ChatWebApplicationFactory NewFactory(MovingClock clock)
        => new(enabled: true, configureServices: clock.Register);

    private static DateTime Day(int month, int day) => new(2026, month, day, 15, 0, 0, DateTimeKind.Utc);

    private static string Stamp(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTime DetectedAt(JsonElement list) => list.GetProperty("detectedAtUtc").GetDateTime().ToUniversalTime();

    private static async Task<ErrorDto> ErrorOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ErrorDto>())!;

    private static async Task<JsonElement> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(Recurring);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, string? value)
        => client.PatchAsync($"{Recurring}/{id}", JsonContent.Create(new Dictionary<string, string?> { ["override"] = value }));

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "PATCH") request.Content = JsonContent.Create(new Dictionary<string, string?> { ["override"] = "NotRecurring" });
        return client.SendAsync(request);
    }

    /// <summary>One expense through the API, as the app creates it. Answers its id.</summary>
    private static async Task<Guid> ChargeAsync(
        HttpClient client, string? merchant, decimal amount, DateTime whenUtc, string category, string? description = null)
    {
        var response = await client.PostAsJsonAsync(Transactions, new
        {
            Amount = amount,
            Currency = "BRL",
            EventTimestampUtc = whenUtc,
            Description = description,
            Merchant = merchant,
            Category = category,
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Member> RegisterAsync(ChatWebApplicationFactory factory, string name)
    {
        var client = factory.CreateClient();
        var register = await client.PostAsJsonAsync(
            "/api/v1/auth/register", new { Email = $"rec-{Guid.NewGuid():N}@example.com", Name = name, Password = "SecurePass123!" });
        register.EnsureSuccessStatusCode();
        var body = await register.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return new Member(client, body.GetProperty("user").GetProperty("id").GetGuid(), Guid.Empty);
    }

    private static async Task<(Member Member, string JoinCode)> OwnerAsync(ChatWebApplicationFactory factory, string name)
    {
        var member = await RegisterAsync(factory, name);
        var created = await member.Client.PostAsJsonAsync("/api/v1/couples", new { });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        member.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return (member with { CoupleId = body.GetProperty("coupleId").GetGuid() }, body.GetProperty("joinCode").GetString()!);
    }

    private static async Task<(Member Owner, Member Other)> TwoMembersAsync(ChatWebApplicationFactory factory, string ownerName, string otherName)
    {
        var (owner, joinCode) = await OwnerAsync(factory, ownerName);
        var other = await RegisterAsync(factory, otherName);
        var joined = await other.Client.PostAsJsonAsync("/api/v1/couples/join", new { JoinCode = joinCode });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var body = await joined.Content.ReadFromJsonAsync<JsonElement>();
        other.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return (owner, other with { CoupleId = owner.CoupleId });
    }

    private sealed class MovingClock : IDateTimeProvider
    {
        public MovingClock(DateTime utcNow) => UtcNow = utcNow;

        public DateTime UtcNow { get; set; }

        public void Register(IServiceCollection services)
        {
            services.RemoveAll<IDateTimeProvider>();
            services.AddSingleton<IDateTimeProvider>(this);
        }
    }
}
