using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.TestSupport;
using static CoupleSync.IntegrationTests.OpenFinance.OpenFinanceSyncKit;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>
/// Issue #27 — money that came in (salary, a Pix received) waits in the review and becomes an income of the month
/// only when someone of the group says so. Pluggy is <see cref="FakePluggyServer"/>; every name and value is invented.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinanceIncomeReviewTests
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

    /// <summary>Noon in Brasília, ten days ago.</summary>
    private static DateTime Noon(int daysAfterTheBase = 0) => Today.AddDays(-10 + daysAfterTheBase).AddHours(15);

    private static string MonthOf(DateTime instant) => BrazilTime.MonthOf(instant);

    private static FakeTransaction Credit(string id, DateTime date, decimal amount, string? description = "Pagamento Empresa Exemplo")
        => new(id, date, amount) { Type = "CREDIT", Description = description };

    private static FakeTransaction Debit(string id, DateTime date, decimal amount, string description = "Compra Exemplo")
        => new(id, date, -amount) { Description = description };

    private static void Bank(OpenFinanceApiFactory factory, string account, params FakeTransaction[] rows)
        => factory.Pluggy.Transactions[account] = rows.ToList();

    private static async Task<(Member Ana, Guid Connection)> SyncedAsync(OpenFinanceApiFactory factory, params FakeTransaction[] checking)
    {
        var ana = await factory.RegisterAsync("Ana Exemplo Souza");
        var connection = await ConnectWithBankAsync(ana);
        Bank(factory, Checking, checking);
        await DoneAsync(factory, ana, connection);
        return (ana, connection);
    }

    private static async Task DoneAsync(OpenFinanceApiFactory factory, Member member, Guid connection)
    {
        var run = await SyncAsync(factory, member, connection);
        Assert.True("Done" == (string)run["status"]!, $"{run["status"]}: {run["error_code"]} {run["error_message"]}");
    }

    private static string Sql(Guid id) => id.ToString().ToUpperInvariant();

    private static async Task<Guid> LineIdAsync(OpenFinanceApiFactory factory, string pluggyId)
        => Guid.Parse((string)(await MirrorRowAsync(factory, pluggyId))["id"]!);

    private static Task<HttpResponseMessage> ConfirmAsync(Member member, object body)
        => member.Client.PostAsJsonAsync($"{Base}/review/confirm", body);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(HttpStatusCode.OK == response.StatusCode, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> ReviewAsync(Member member, DateTime date)
        => await member.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={MonthOf(date)}");

    private static bool Has(JsonElement list, Guid id) => list.EnumerateArray().Any(l => l.GetProperty("id").GetGuid() == id);

    private static Task<List<Dictionary<string, object?>>> IncomesAsync(OpenFinanceApiFactory factory)
        => factory.RowsAsync("SELECT * FROM income_sources ORDER BY created_at_utc, name");

    private static decimal Money(object? value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    // ================================================================ the review lists the entries

    [Fact]
    public async Task TheReview_ListsTheEntriesWaiting_ApartFromTheExpenses_AndCountsThemByMonth()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory,
            Credit("t27-salary", Noon(), 3500.00m),
            Credit("t27-pix-in", Noon().AddHours(1), 120.00m, "Pix recebido"),
            Credit("t27-dollars", Noon().AddHours(1), 50.00m, "Remessa") with { CurrencyCode = "USD" },
            Credit("t27-not-settled", Noon().AddHours(1), 70.00m, "Crédito a efetivar") with { Status = "PENDING" },
            Debit("t27-expense", Noon(), 25.00m));
        var salary = await LineIdAsync(factory, "t27-salary");
        var expense = await LineIdAsync(factory, "t27-expense");

        var review = await ReviewAsync(ana, Noon());

        var incomes = review.GetProperty("incomes");
        Assert.Equal(4, incomes.GetArrayLength());
        var line = incomes.EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == salary);
        Assert.Equal("Credit", line.GetProperty("type").GetString());
        Assert.Equal(3500.00m, line.GetProperty("amount").GetDecimal());
        Assert.Equal("BRL", line.GetProperty("currency").GetString());
        Assert.Equal("Pagamento Empresa Exemplo", line.GetProperty("description").GetString());
        Assert.Equal("Banco Exemplo", line.GetProperty("bankName").GetString());
        Assert.Equal("Posted", line.GetProperty("bankStatus").GetString());
        // The expenses are what they always were: no entry among them, and their counts do not count entries.
        Assert.False(Has(review.GetProperty("expenses"), salary));
        Assert.True(Has(review.GetProperty("expenses"), expense));
        Assert.False(Has(incomes, expense));
        Assert.Equal(1, review.GetProperty("pendingAllMonths").GetInt32());
        Assert.Equal(25.00m, review.GetProperty("pendingTotalBrl").GetDecimal());
        // What can become an income: settled at the bank, with a value, in reais.
        Assert.Equal(2, review.GetProperty("pendingIncomesAllMonths").GetInt32());
        var months = review.GetProperty("pendingIncomesByMonth").EnumerateArray()
            .ToDictionary(m => m.GetProperty("month").GetString()!, m => m.GetProperty("pending").GetInt32());
        Assert.Equal(4, months.Values.Sum());
        Assert.Contains(MonthOf(Noon()), months.Keys);
    }

    [Fact]
    public async Task AnEntryIgnoredAutomatically_IsNotAmongTheEntriesWaiting_AndAnotherGroupSeesNone()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await SyncedAsync(factory, Credit("t27-waits", Noon(), 900.00m));
        // The same money leaving another account of the group on the same day: a transfer, ignored on both sides.
        var bruno = await factory.RegisterAsync("Bruno Modelo Reis", ana.JoinCode);
        var brunoConnection = await ConnectAsync(bruno, "Bancos do Bruno");
        await AddItemAsync(bruno, brunoConnection, FakePluggyServer.OtherItemWithAccounts);
        Bank(factory, OtherChecking, Debit("t27-leg-out", Noon(), 400.00m, "Transferência enviada"));
        Bank(factory, Checking, Credit("t27-waits", Noon(), 900.00m), Credit("t27-leg-in", Noon().AddHours(1), 400.00m, "Transferência recebida"));
        await DoneAsync(factory, bruno, brunoConnection);
        await DoneAsync(factory, ana, connection);
        var carla = await factory.RegisterAsync("Carla Modelo Dias"); // another group

        var review = await ReviewAsync(bruno, Noon());

        Assert.Equal(new[] { await LineIdAsync(factory, "t27-waits") }, review.GetProperty("incomes").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()));
        Assert.True(Has(review.GetProperty("ignored"), await LineIdAsync(factory, "t27-leg-in")));
        var carlas = await ReviewAsync(carla, Noon());
        Assert.Empty(carlas.GetProperty("incomes").EnumerateArray());
        Assert.Equal(0, carlas.GetProperty("pendingIncomesAllMonths").GetInt32());
    }

    // ================================================================ an entry becomes an income of the month

    [Fact]
    public async Task ConfirmingAnEntry_CreatesASharedNonRecurringIncome_OfWhoConnected_AndLinksTheLine()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory, Credit("t27-salary", Noon(), 3500.55m));
        var bruno = await factory.RegisterAsync("Bruno Modelo Reis", ana.JoinCode);
        var line = await LineIdAsync(factory, "t27-salary");

        // Anyone of the group confirms; the income belongs to who connected the account.
        var result = await OkAsync(ConfirmAsync(bruno, new { incomes = new[] { new { id = line } } }));

        var income = Assert.Single(await IncomesAsync(factory));
        Assert.Equal(Sql(ana.CoupleId), income["couple_id"]);
        Assert.Equal(Sql(ana.UserId), income["user_id"]);
        Assert.Equal(MonthOf(Noon()), income["month"]);
        Assert.Equal("Pagamento Empresa Exemplo", income["name"]);
        Assert.Equal(3500.55m, Money(income["amount"]));
        Assert.Equal("BRL", income["currency"]);
        Assert.Equal(1L, Convert.ToInt64(income["is_shared"], CultureInfo.InvariantCulture));
        Assert.Equal(0L, Convert.ToInt64(income["is_recurring"], CultureInfo.InvariantCulture));

        var created = Assert.Single(result.GetProperty("incomes").EnumerateArray());
        Assert.Equal(line, created.GetProperty("id").GetGuid());
        Assert.Equal((string)income["id"]!, Sql(created.GetProperty("incomeSourceId").GetGuid()));
        Assert.Empty(result.GetProperty("created").EnumerateArray());
        Assert.Equal(0, result.GetProperty("alreadyConfirmed").GetInt32());

        var row = await MirrorRowAsync(factory, "t27-salary");
        Assert.Equal("Confirmed", row["review_state"]);
        Assert.Equal(income["id"], row["linked_income_source_id"]);
        Assert.Null(row["linked_transaction_id"]);
        Assert.NotNull(row["reviewed_at_utc"]);
        // No transaction was created: an entry is not an expense.
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        // It left the review.
        Assert.Empty((await ReviewAsync(ana, Noon())).GetProperty("incomes").EnumerateArray());

        // The incomes of the month show it, marked as come from the bank; a typed one is not marked.
        var typed = await ana.Client.PostAsJsonAsync("/api/v1/incomes", new { month = MonthOf(Noon()), name = "Freela", amount = 100m, currency = "BRL", isShared = true });
        Assert.True(typed.IsSuccessStatusCode, await typed.Content.ReadAsStringAsync());
        Assert.False((await JsonAsync(typed)).GetProperty("fromBank").GetBoolean());
        var month = await ana.Client.GetFromJsonAsync<JsonElement>($"/api/v1/incomes/{MonthOf(Noon())}");
        var shared = month.GetProperty("sharedIncome").GetProperty("sources").EnumerateArray()
            .ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("fromBank").GetBoolean());
        Assert.Equal(new Dictionary<string, bool> { ["Pagamento Empresa Exemplo"] = true, ["Freela"] = false }, shared);
        Assert.Equal(3600.55m, month.GetProperty("coupleTotal").GetDecimal());
    }

    [Fact]
    public async Task TheMonthOfTheIncome_IsTheMonthOfTheDayInBrazil()
    {
        await using var factory = NewFactory();
        // 01:30 UTC of the first day of this month: 22:30 of the last day of the month before, in Brasília.
        var firstOfMonth = new DateTime(Today.Year, Today.Month, 1, 1, 30, 0, DateTimeKind.Utc);
        // A date the bank sends with no time (midnight UTC) is the day as written: the first, this month.
        var (ana, _) = await SyncedAsync(factory,
            Credit("t27-last-night-of-the-month", firstOfMonth, 800.00m, "Pix da virada"),
            Credit("t27-first-day-no-time", firstOfMonth.Date, 900.00m, "Depósito"));

        await OkAsync(ConfirmAsync(ana, new
        {
            incomes = new[]
            {
                new { id = await LineIdAsync(factory, "t27-last-night-of-the-month") },
                new { id = await LineIdAsync(factory, "t27-first-day-no-time") },
            },
        }));

        var months = (await IncomesAsync(factory)).ToDictionary(r => (string)r["name"]!, r => (string)r["month"]!);
        var thisMonth = firstOfMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var monthBefore = firstOfMonth.AddDays(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        Assert.Equal(monthBefore, months["Pix da virada"]);
        Assert.Equal(thisMonth, months["Depósito"]);
    }

    [Fact]
    public async Task TheNameIsTheOneInformed_OrTheDescription_OrAFixedText_AlwaysWithinTheLimitOfAName()
    {
        await using var factory = NewFactory();
        var longDescription = "Transferência recebida de Empresa Exemplo de Serviços Administrativos e Participações Ltda";
        var (ana, _) = await SyncedAsync(factory,
            Credit("t27-named", Noon(), 10.00m, "Pix recebido"),
            Credit("t27-blank-name", Noon(), 20.00m, "Depósito em dinheiro"),
            Credit("t27-long", Noon(), 30.00m, longDescription),
            Credit("t27-nothing", Noon(), 40.00m, description: null));

        await OkAsync(ConfirmAsync(ana, new
        {
            incomes = new object[]
            {
                new { id = await LineIdAsync(factory, "t27-named"), name = "  Salário da Ana  " },
                new { id = await LineIdAsync(factory, "t27-blank-name"), name = "   " },
                new { id = await LineIdAsync(factory, "t27-long") },
                new { id = await LineIdAsync(factory, "t27-nothing") },
            },
        }));

        var names = (await IncomesAsync(factory)).ToDictionary(r => Money(r["amount"]), r => (string)r["name"]!);
        Assert.Equal("Salário da Ana", names[10.00m]);
        Assert.Equal("Depósito em dinheiro", names[20.00m]);
        Assert.Equal(longDescription[..64].Trim(), names[30.00m]);
        Assert.Equal("Entrada do banco", names[40.00m]);

        // A name longer than an income can have is refused before anything is read.
        Assert.Equal("Confirmed", (await MirrorRowAsync(factory, "t27-named"))["review_state"]);
        var tooLong = await ConfirmAsync(ana, new { incomes = new[] { new { id = Guid.NewGuid(), name = new string('n', 65) } } });
        await AssertErrorAsync(tooLong, HttpStatusCode.BadRequest, "VALIDATION_ERROR");
    }

    [Fact]
    public async Task ConfirmingTheSameEntryTwice_CreatesOneIncomeOnly()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory, Credit("t27-twice", Noon(), 1000.00m));
        var body = new { incomes = new[] { new { id = await LineIdAsync(factory, "t27-twice") } } };

        var first = await OkAsync(ConfirmAsync(ana, body));
        var second = await OkAsync(ConfirmAsync(ana, body));

        Assert.Single(first.GetProperty("incomes").EnumerateArray());
        Assert.Empty(second.GetProperty("incomes").EnumerateArray());
        Assert.Equal(1, second.GetProperty("alreadyConfirmed").GetInt32());
        Assert.Single(await IncomesAsync(factory));
    }

    [Fact]
    public async Task TwoRequestsConfirmingTheSameEntryAtOnce_OneWins_TheOtherGets409_AndThereIsOneIncome()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory, Credit("t27-raced", Noon(), 640.00m));
        var line = await LineIdAsync(factory, "t27-raced");
        var body = new { incomes = new[] { new { id = line } } };
        // Right before this request saves, the other one (the same body) is served whole.
        JsonElement? other = null;
        factory.BeforeNextSave = async () => other = await OkAsync(ConfirmAsync(ana, body));

        var loser = await ConfirmAsync(ana, body);

        await AssertErrorAsync(loser, HttpStatusCode.Conflict, "BANK_REVIEW_CONFLICT");
        Assert.Single(other!.Value.GetProperty("incomes").EnumerateArray());
        var income = Assert.Single(await IncomesAsync(factory));
        Assert.Equal(income["id"], (await MirrorRowAsync(factory, "t27-raced"))["linked_income_source_id"]);
    }

    [Fact]
    public async Task TwoEntriesWithTheSameDescriptionInTheSameMonth_BecomeTwoIncomes_WithDifferentNames_AlsoNextToATypedOne()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory,
            Credit("t27-pix-1", Noon(), 50.00m, "Pix recebido"),
            Credit("t27-pix-2", Noon().AddHours(1), 60.00m, "Pix recebido"),
            Credit("t27-pix-3", Noon().AddHours(2), 70.00m, "PIX RECEBIDO"));
        // The same name, typed by hand in the same month (whatever the case): the incomes of a month are told apart by name.
        var typed = await ana.Client.PostAsJsonAsync("/api/v1/incomes", new { month = MonthOf(Noon()), name = "pix recebido", amount = 5m, currency = "BRL", isShared = false });
        Assert.True(typed.IsSuccessStatusCode, await typed.Content.ReadAsStringAsync());

        await OkAsync(ConfirmAsync(ana, new { incomes = new[] { new { id = await LineIdAsync(factory, "t27-pix-1") }, new { id = await LineIdAsync(factory, "t27-pix-2") } } }));
        await OkAsync(ConfirmAsync(ana, new { incomes = new[] { new { id = await LineIdAsync(factory, "t27-pix-3") } } }));

        var names = (await IncomesAsync(factory)).ToDictionary(r => Money(r["amount"]), r => (string)r["name"]!);
        Assert.Equal("pix recebido", names[5m]);
        Assert.Equal("Pix recebido (2)", names[50.00m]);
        Assert.Equal("Pix recebido (3)", names[60.00m]);
        Assert.Equal("PIX RECEBIDO (4)", names[70.00m]);
        // Every one of them counts in the month (none replaces another).
        var month = await ana.Client.GetFromJsonAsync<JsonElement>($"/api/v1/incomes/{MonthOf(Noon())}");
        Assert.Equal(185.00m, month.GetProperty("coupleTotal").GetDecimal());
    }

    [Fact]
    public async Task AnEntryWithoutAValue_OrInAnotherCurrency_IsSkipped_AndNothingIsCreatedForIt()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory,
            Credit("t27-zero", Noon(), 0m, "Ajuste"),
            Credit("t27-dollars", Noon(), 50.00m, "Remessa") with { CurrencyCode = "USD" },
            Credit("t27-good", Noon(), 80.00m, "Pix recebido"));
        var zero = await LineIdAsync(factory, "t27-zero");
        var dollars = await LineIdAsync(factory, "t27-dollars");
        var good = await LineIdAsync(factory, "t27-good");

        var result = await OkAsync(ConfirmAsync(ana, new { incomes = new[] { new { id = zero }, new { id = dollars }, new { id = good } } }));

        Assert.Equal(new[] { zero, dollars }.Order(), result.GetProperty("skipped").EnumerateArray().Select(e => e.GetGuid()).Order());
        Assert.Equal(new[] { dollars }, result.GetProperty("skippedOtherCurrency").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(new[] { good }, result.GetProperty("incomes").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
        var income = Assert.Single(await IncomesAsync(factory));
        Assert.Equal((80.00m, "BRL"), (Money(income["amount"]), (string)income["currency"]!));
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t27-zero"))["review_state"]);
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t27-dollars"))["review_state"]);
    }

    [Fact]
    public async Task Confirm_RefusesAnIncomeThatMakesNoSense_InPortuguese_AndStoresNothing()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await SyncedAsync(factory,
            Credit("t27-entry", Noon(), 100.00m),
            Credit("t27-not-settled", Noon(), 70.00m) with { Status = "PENDING" },
            Debit("t27-expense", Noon(), 25.00m));
        var entry = await LineIdAsync(factory, "t27-entry");
        var notSettled = await LineIdAsync(factory, "t27-not-settled");
        var expense = await LineIdAsync(factory, "t27-expense");
        var carla = await factory.RegisterAsync("Carla Modelo Dias"); // another group

        // An expense is not an income.
        await AssertErrorAsync(await ConfirmAsync(ana, new { incomes = new[] { new { id = expense } } }),
            HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", "Despesas não são registradas como renda.");
        // And an entry is still not an expense, nor the same as a transaction.
        await AssertErrorAsync(await ConfirmAsync(ana, new { expenses = new[] { new { id = entry } } }),
            HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", "Entradas não são confirmadas como despesa.");
        // Not settled at the bank yet.
        await AssertErrorAsync(await ConfirmAsync(ana, new { incomes = new[] { new { id = notSettled } } }),
            HttpStatusCode.UnprocessableEntity, "TRANSACTION_NOT_POSTED");
        // One destination at a time, each line once.
        await AssertErrorAsync(await ConfirmAsync(ana, new { incomes = new[] { new { id = entry } }, discard = new[] { entry } }),
            HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", "Um lançamento só pode ter um destino por vez.");
        await AssertErrorAsync(await ConfirmAsync(ana, new { incomes = new[] { new { id = entry }, new { id = entry } } }),
            HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", "O mesmo lançamento foi enviado mais de uma vez.");
        await AssertErrorAsync(await ConfirmAsync(ana, new { incomes = new object?[] { null } }),
            HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", "Há um lançamento inválido na lista.");
        await AssertErrorAsync(await ConfirmAsync(ana, new { incomes = Enumerable.Range(0, 501).Select(_ => new { id = Guid.NewGuid() }) }),
            HttpStatusCode.BadRequest, "VALIDATION_ERROR");
        // A line of another group does not exist.
        await AssertErrorAsync(await ConfirmAsync(carla, new { incomes = new[] { new { id = entry } } }),
            HttpStatusCode.NotFound, "BANK_TRANSACTION_NOT_FOUND");
        // A line already discarded is not confirmed without being restored first.
        await OkAsync(ConfirmAsync(ana, new { discard = new[] { entry } }));
        await AssertErrorAsync(await ConfirmAsync(ana, new { incomes = new[] { new { id = entry } } }),
            HttpStatusCode.Conflict, "BANK_TRANSACTION_NOT_PENDING");

        Assert.Empty(await IncomesAsync(factory));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t27-expense"))["review_state"]);
        _ = connection;
    }

    [Fact]
    public async Task AnEntryCanBeDiscarded_IsListedAmongTheDiscarded_AndRestoredGoesBackToTheEntries()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory, Credit("t27-not-income", Noon(), 15.00m, "Estorno"));
        var line = await LineIdAsync(factory, "t27-not-income");

        var discarded = await OkAsync(ConfirmAsync(ana, new { discard = new[] { line } }));

        Assert.Equal(new[] { line }, discarded.GetProperty("discarded").EnumerateArray().Select(e => e.GetGuid()));
        var review = await ReviewAsync(ana, Noon());
        Assert.Empty(review.GetProperty("incomes").EnumerateArray());
        var listed = review.GetProperty("discarded").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == line);
        Assert.Equal("Credit", listed.GetProperty("type").GetString());
        Assert.Empty(await IncomesAsync(factory));

        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { line }));

        review = await ReviewAsync(ana, Noon());
        Assert.True(Has(review.GetProperty("incomes"), line));
        Assert.False(Has(review.GetProperty("discarded"), line));
    }

    [Fact]
    public async Task DeletingTheIncome_SendsTheLineBackAsDiscarded_AndItCanBeRestoredAndConfirmedAgain()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory, Credit("t27-deleted", Noon(), 2200.00m, "Salário Exemplo"));
        var bruno = await factory.RegisterAsync("Bruno Modelo Reis", ana.JoinCode);
        var line = await LineIdAsync(factory, "t27-deleted");
        var confirmed = await OkAsync(ConfirmAsync(ana, new { incomes = new[] { new { id = line } } }));
        var incomeId = confirmed.GetProperty("incomes")[0].GetProperty("incomeSourceId").GetGuid();

        // The income is shared: anyone of the group deletes it.
        Assert.Equal(HttpStatusCode.NoContent, (await bruno.Client.DeleteAsync($"/api/v1/incomes/{incomeId}")).StatusCode);

        Assert.Empty(await IncomesAsync(factory));
        var row = await MirrorRowAsync(factory, "t27-deleted");
        Assert.Equal("Discarded", row["review_state"]);
        Assert.Null(row["linked_income_source_id"]);
        var review = await ReviewAsync(ana, Noon());
        Assert.True(Has(review.GetProperty("discarded"), line));
        Assert.False(Has(review.GetProperty("incomes"), line));

        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { line }));
        var again = await OkAsync(ConfirmAsync(ana, new { incomes = new[] { new { id = line, name = "Salário de novo" } } }));

        Assert.Single(again.GetProperty("incomes").EnumerateArray());
        Assert.Equal("Salário de novo", Assert.Single(await IncomesAsync(factory))["name"]);
    }

    [Fact]
    public async Task DeletingAnIncomeTypedByHand_TouchesNoLineOfTheBank()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory, Credit("t27-other", Noon(), 300.00m));
        var line = await LineIdAsync(factory, "t27-other");
        await OkAsync(ConfirmAsync(ana, new { incomes = new[] { new { id = line } } }));
        var typed = await ana.Client.PostAsJsonAsync("/api/v1/incomes", new { month = MonthOf(Noon()), name = "Freela", amount = 100m, currency = "BRL", isShared = true });
        var typedId = (await JsonAsync(typed)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/incomes/{typedId}")).StatusCode);

        Assert.Equal("Confirmed", (await MirrorRowAsync(factory, "t27-other"))["review_state"]);
        Assert.Single(await IncomesAsync(factory));
    }

    [Fact]
    public async Task AConfirmedEntryWhoseIncomeIsGone_CountsAsDiscarded_AndCanBeRestored()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory, Credit("t27-orphan", Noon(), 410.00m));
        var line = await LineIdAsync(factory, "t27-orphan");
        await OkAsync(ConfirmAsync(ana, new { incomes = new[] { new { id = line } } }));
        // The income was deleted at the very moment of the confirmation: the database cleared the link (ON DELETE SET NULL).
        await factory.ExecuteAsync($"UPDATE bank_transactions SET linked_income_source_id = NULL WHERE id = '{Sql(line)}'");
        await factory.ExecuteAsync("DELETE FROM income_sources");

        var review = await ReviewAsync(ana, Noon());
        Assert.True(Has(review.GetProperty("discarded"), line));
        Assert.False(Has(review.GetProperty("incomes"), line));
        // It is not "already confirmed": confirming it again without restoring is refused.
        await AssertErrorAsync(await ConfirmAsync(ana, new { incomes = new[] { new { id = line } } }), HttpStatusCode.Conflict, "BANK_TRANSACTION_NOT_PENDING");

        var restored = await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { line }));
        Assert.Equal(new[] { line }, restored.GetProperty("restored").EnumerateArray().Select(e => e.GetGuid()));
        Assert.True(Has((await ReviewAsync(ana, Noon())).GetProperty("incomes"), line));
    }

    [Fact]
    public async Task TheLimitOfIncomesOfAPersonInAMonth_AlsoHoldsForTheOnesFromTheBank_AllOrNothing()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory,
            Credit("t27-limit-1", Noon(), 11.00m, "Pix um"),
            Credit("t27-limit-2", Noon(), 12.00m, "Pix dois"));
        for (var i = 1; i <= 19; i++)
        {
            var typed = await ana.Client.PostAsJsonAsync("/api/v1/incomes", new { month = MonthOf(Noon()), name = $"Renda {i}", amount = 1m, currency = "BRL", isShared = false });
            Assert.True(typed.IsSuccessStatusCode, await typed.Content.ReadAsStringAsync());
        }

        var one = await LineIdAsync(factory, "t27-limit-1");
        var two = await LineIdAsync(factory, "t27-limit-2");
        var tooMany = await ConfirmAsync(ana, new { incomes = new[] { new { id = one }, new { id = two } } });

        await AssertErrorAsync(tooMany, HttpStatusCode.UnprocessableEntity, "INCOME_SOURCE_LIMIT");
        Assert.Equal(19, (await IncomesAsync(factory)).Count);
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t27-limit-1"))["review_state"]);

        // One still fits.
        await OkAsync(ConfirmAsync(ana, new { incomes = new[] { new { id = one } } }));
        Assert.Equal(20, (await IncomesAsync(factory)).Count);
    }

    [Fact]
    public async Task ExpensesAndIncomesAndDiscards_GoInTheSameRequest_AndTheOldRequestWithoutIncomesMeansWhatItAlwaysMeant()
    {
        await using var factory = NewFactory();
        var (ana, _) = await SyncedAsync(factory,
            Credit("t27-mixed-in", Noon(), 500.00m, "Salário Exemplo"),
            Credit("t27-mixed-discard", Noon(), 5.00m, "Estorno"),
            Debit("t27-mixed-out", Noon(), 25.00m),
            Debit("t27-old-request", Noon(), 35.00m));
        var income = await LineIdAsync(factory, "t27-mixed-in");
        var expense = await LineIdAsync(factory, "t27-mixed-out");
        var discard = await LineIdAsync(factory, "t27-mixed-discard");

        var result = await OkAsync(ConfirmAsync(ana, new { expenses = new[] { new { id = expense } }, incomes = new[] { new { id = income } }, discard = new[] { discard } }));

        Assert.Single(result.GetProperty("created").EnumerateArray());
        Assert.Single(result.GetProperty("incomes").EnumerateArray());
        Assert.Single(result.GetProperty("discarded").EnumerateArray());
        Assert.Single(await IncomesAsync(factory));
        Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"));

        // The body the installed app sends: no "incomes" at all.
        var old = await OkAsync(ConfirmAsync(ana, new { expenses = new[] { new { id = await LineIdAsync(factory, "t27-old-request") } } }));
        Assert.Single(old.GetProperty("created").EnumerateArray());
        Assert.Empty(old.GetProperty("incomes").EnumerateArray());
        Assert.Single(await IncomesAsync(factory));
    }
}
