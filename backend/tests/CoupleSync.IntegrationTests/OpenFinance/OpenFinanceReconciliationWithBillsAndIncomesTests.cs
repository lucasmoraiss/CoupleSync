using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.TestSupport;
using static CoupleSync.IntegrationTests.OpenFinance.OpenFinanceSyncKit;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>
/// Issues #26 and #27 together — the judgement of suspected duplicates (when a line arrives, at the start of every
/// run, when a line is restored, at the moment of confirming) next to what #27 decides about a line: money that came
/// in (it becomes an income, never a suspected duplicate) and the payment of a bill recognised by its value.
/// Pluggy is <see cref="FakePluggyServer"/>; every name and value is invented.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinanceReconciliationWithBillsAndIncomesTests
{
    private const string Checking = FakePluggyServer.CheckingAccountId;
    private const string Card = FakePluggyServer.CreditCardAccountId;
    private const string OtherChecking = FakePluggyServer.OtherCheckingAccountId;

    private static OpenFinanceApiFactory NewFactory()
    {
        var factory = new OpenFinanceApiFactory { FastSync = true };
        foreach (var account in new[] { Checking, Card, OtherChecking }) factory.Pluggy.Transactions[account] = [];
        return factory;
    }

    private static readonly DateTime Today = DateTime.UtcNow.Date;

    /// <summary>Noon in Brasília, ten days ago (inside the first window of a connection).</summary>
    private static DateTime Noon(int daysAfterTheBase = 0) => Today.AddDays(-10 + daysAfterTheBase).AddHours(15);

    private static FakeTransaction Debit(string id, DateTime date, decimal amount, string description = "Compra Exemplo")
        => new(id, date, -amount) { Description = description };

    private static FakeTransaction Credit(string id, DateTime date, decimal amount, string description = "Pagamento Empresa Exemplo")
        => new(id, date, amount) { Type = "CREDIT", Description = description };

    private static void Bank(OpenFinanceApiFactory factory, string account, params FakeTransaction[] rows)
        => factory.Pluggy.Transactions[account] = rows.ToList();

    private static async Task<(Member Ana, Guid Connection)> AnaAsync(OpenFinanceApiFactory factory)
    {
        var ana = await factory.RegisterAsync("Ana Exemplo Souza");
        return (ana, await ConnectWithBankAsync(ana));
    }

    private static async Task DoneAsync(OpenFinanceApiFactory factory, Member member, Guid connection)
    {
        var run = await SyncAsync(factory, member, connection);
        Assert.True("Done" == (string)run["status"]!, $"{run["status"]}: {run["error_code"]} {run["error_message"]}");
    }

    private static async Task<Guid> ManualAsync(Member member, decimal amount, DateTime whenUtc, string description)
    {
        var created = await member.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            amount,
            currency = "BRL",
            description,
            category = "OUTROS",
            eventTimestampUtc = whenUtc,
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await JsonAsync(created)).GetProperty("id").GetGuid();
    }

    private static string Sql(Guid id) => id.ToString().ToUpperInvariant();

    private static async Task<Guid> LineIdAsync(OpenFinanceApiFactory factory, string pluggyId)
        => Guid.Parse((string)(await MirrorRowAsync(factory, pluggyId))["id"]!);

    private static async Task<(string State, string? Reason, string? Matched)> StateAsync(OpenFinanceApiFactory factory, string pluggyId)
    {
        var row = await MirrorRowAsync(factory, pluggyId);
        return ((string)row["review_state"]!, (string?)row["auto_reason"], (string?)row["matched_transaction_id"]);
    }

    private static async Task<JsonElement> ReviewAsync(Member member, DateTime date)
        => await member.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={BrazilTime.MonthOf(date)}");

    private static JsonElement Line(JsonElement list, Guid id)
        => list.EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == id);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(HttpStatusCode.OK == response.StatusCode, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    private static Task<HttpResponseMessage> ConfirmAsync(Member member, object body)
        => member.Client.PostAsJsonAsync($"{Base}/review/confirm", body);

    private static List<Guid> Ids(JsonElement list) => list.EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();

    private static async Task<int> CountAsync(OpenFinanceApiFactory factory, string table)
        => (await factory.RowsAsync($"SELECT id FROM {table}")).Count;

    // ================================================================ money that came in is never a suspected duplicate

    [Fact]
    public async Task AnEntryOfTheSameValueAndDayAsATransactionOfTheApp_IsNeverSuspected_ByAnyJudgement_AndBecomesAnIncome()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        await ManualAsync(ana, 120.00m, Noon(), "Presente");
        Bank(factory, Checking, Credit("t2627-pix-in", Noon(), 120.00m, "Pix recebido"));

        await DoneAsync(factory, ana, connection); // judged when it arrives
        await DoneAsync(factory, ana, connection); // and again, with the whole group, before the bank is read

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t2627-pix-in"));
        var entry = await LineIdAsync(factory, "t2627-pix-in");
        var review = await ReviewAsync(ana, Noon());
        Assert.Equal(JsonValueKind.Null, Line(review.GetProperty("incomes"), entry).GetProperty("match").ValueKind);
        Assert.Equal(0, review.GetProperty("suspectedDuplicates").GetInt32());

        // Ignored by the person's hand and brought back (the judgement of a line that comes back): still no suspicion.
        await OkAsync(ConfirmAsync(ana, new { discard = new[] { entry } }));
        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { entry }));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t2627-pix-in"));

        // Registered with the flag of "Selecionar tudo": the flag is about the expenses, the entry is never held.
        var body = await OkAsync(ConfirmAsync(ana, new { incomes = new[] { new { id = entry } }, holdPossibleDuplicates = true }));

        Assert.Equal(new[] { entry }, Ids(body.GetProperty("incomes")));
        Assert.Empty(body.GetProperty("heldAsPossibleDuplicates").EnumerateArray());
        Assert.Equal(1, await CountAsync(factory, "income_sources"));
        Assert.Equal(1, await CountAsync(factory, "transactions"));
        Assert.Equal("Confirmed", (await StateAsync(factory, "t2627-pix-in")).State);
    }

    [Fact]
    public async Task OneRequest_HoldsThePossibleDuplicate_ConfirmsTheOtherExpense_AndRegistersTheIncome()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var typed = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        Bank(factory, Checking,
            Debit("t2627-padaria", Noon(), 25.00m),
            Debit("t2627-plain", Noon(), 80.00m),
            Credit("t2627-salary", Noon(), 3500.00m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(typed)), await StateAsync(factory, "t2627-padaria"));
        var padaria = await LineIdAsync(factory, "t2627-padaria");
        var plain = await LineIdAsync(factory, "t2627-plain");
        var salary = await LineIdAsync(factory, "t2627-salary");

        var body = await OkAsync(ConfirmAsync(ana, new
        {
            expenses = new[] { new { id = padaria }, new { id = plain } },
            incomes = new[] { new { id = salary } },
            holdPossibleDuplicates = true,
        }));

        Assert.Equal(new[] { padaria }, body.GetProperty("heldAsPossibleDuplicates").EnumerateArray().Select(id => id.GetGuid()));
        Assert.Equal(new[] { plain }, Ids(body.GetProperty("created")));
        Assert.Equal(new[] { salary }, Ids(body.GetProperty("incomes")));
        Assert.Equal(("Pending", null, Sql(typed)), await StateAsync(factory, "t2627-padaria"));
        Assert.Equal("Confirmed", (await StateAsync(factory, "t2627-plain")).State);
        var salaryRow = await MirrorRowAsync(factory, "t2627-salary");
        Assert.Equal("Confirmed", salaryRow["review_state"]);
        Assert.NotNull(salaryRow["linked_income_source_id"]);
        Assert.Equal(2, await CountAsync(factory, "transactions")); // the typed one and the plain one
        Assert.Equal(1, await CountAsync(factory, "income_sources"));
    }

    // ================================================================ the payment of a bill by its value and the suspicion

    [Fact]
    public async Task ADebitThatPaysABillByItsValue_IsIgnored_EvenLookingLikeATransactionOfTheApp_AndTheNextOneOfThatValueIsTheSuspectedOne()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var typed = await ManualAsync(ana, 1500.25m, Noon(), "Cartão");
        var due = DateOnly.FromDateTime(Noon(3));
        factory.Pluggy.Bills[Card] = [new FakeBill("bill-2627", due, 1500.25m) { ClosingDate = due.AddDays(-7) }];
        Bank(factory, Checking, Debit("t2627-pays", Noon(), 1500.25m, "Débito automático"));

        await DoneAsync(factory, ana, connection);

        // The bill wins over the suspicion: the line is no expense, and points to nothing.
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t2627-pays"));

        // The judgement of the whole group, at the start of the next run, leaves the ignored line alone; another debit
        // of the same value pays no bill (each bill has one payment) and is the one that looks like the typed one.
        Bank(factory, Checking,
            Debit("t2627-pays", Noon(), 1500.25m, "Débito automático"),
            Debit("t2627-other", Noon(1), 1500.25m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t2627-pays"));
        Assert.Equal(("Pending", null, Sql(typed)), await StateAsync(factory, "t2627-other"));

        // Brought back by the person: it waits, pointing to what it looks like, and no run ignores it again.
        var pays = await LineIdAsync(factory, "t2627-pays");
        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { pays }));
        Assert.Equal(("Pending", null, Sql(typed)), await StateAsync(factory, "t2627-pays"));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(typed)), await StateAsync(factory, "t2627-pays"));
        Assert.Equal(("Pending", null, Sql(typed)), await StateAsync(factory, "t2627-other"));
    }
}
