using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.TestSupport;
using static CoupleSync.IntegrationTests.OpenFinance.OpenFinanceSyncKit;

namespace CoupleSync.IntegrationTests.OpenFinance;

/// <summary>
/// Issue #26 — what a synchronisation decides before anyone looks (transfers between accounts of the group, bill
/// payments, credits from someone of the group, suspected duplicates) and what the review does with it ("it is the
/// same", "it is another one", restoring what was ignored). Pluggy is <see cref="FakePluggyServer"/>; every name and
/// value is invented.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class OpenFinanceReconciliationTests
{
    private const string Checking = FakePluggyServer.CheckingAccountId;
    private const string Card = FakePluggyServer.CreditCardAccountId;
    private const string OtherChecking = FakePluggyServer.OtherCheckingAccountId;

    private static OpenFinanceApiFactory NewFactory()
    {
        var factory = new OpenFinanceApiFactory { FastSync = true };
        // Each test says what the banks have.
        foreach (var account in new[] { Checking, Card, OtherChecking }) factory.Pluggy.Transactions[account] = [];
        return factory;
    }

    /// <summary>The day every date here is counted from, read once: a test that crosses midnight (UTC) still agrees with itself.</summary>
    private static readonly DateTime Today = DateTime.UtcNow.Date;

    /// <summary>Noon in Brasília, some days ago (inside the first window of a connection, in a month that is over or not).</summary>
    private static DateTime Noon(int daysAfterTheBase = 0) => Today.AddDays(-10 + daysAfterTheBase).AddHours(15);

    private static FakeTransaction Debit(string id, DateTime date, decimal amount, string description = "Compra Exemplo")
        => new(id, date, -amount) { Description = description };

    private static FakeTransaction Credit(string id, DateTime date, decimal amount, string description = "Transferência recebida")
        => new(id, date, amount) { Type = "CREDIT", Description = description };

    private static void Bank(OpenFinanceApiFactory factory, string account, params FakeTransaction[] rows)
        => factory.Pluggy.Transactions[account] = rows.ToList();

    /// <summary>Ana, with the bank of the fixture: a checking account and a credit card ("Banco Exemplo").</summary>
    private static async Task<(Member Ana, Guid Connection)> AnaAsync(OpenFinanceApiFactory factory)
    {
        var ana = await factory.RegisterAsync("Ana Exemplo Souza");
        return (ana, await ConnectWithBankAsync(ana));
    }

    /// <summary>Someone else, in the group of <paramref name="joinCode"/> or in a new one, with the other bank: a savings account ("Banco Modelo").</summary>
    private static async Task<(Member Member, Guid Connection)> WithOtherBankAsync(OpenFinanceApiFactory factory, string name, string? joinCode)
    {
        var member = await factory.RegisterAsync(name, joinCode);
        var connection = await ConnectAsync(member, $"Bancos de {name}");
        await AddItemAsync(member, connection, FakePluggyServer.OtherItemWithAccounts);
        return (member, connection);
    }

    private static async Task DoneAsync(OpenFinanceApiFactory factory, Member member, Guid connection)
        => Assert.Equal("Done", (await SyncAsync(factory, member, connection))["status"]);

    private static async Task<Guid> ManualAsync(
        Member member, decimal amount, DateTime whenUtc, string description = "Padaria", string? merchant = null, string category = "OUTROS")
    {
        var created = await member.Client.PostAsJsonAsync("/api/v1/transactions", new
        {
            amount,
            currency = "BRL",
            description,
            merchant,
            category,
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

    private static JsonElement? Find(JsonElement list, Guid id)
    {
        foreach (var line in list.EnumerateArray())
            if (line.GetProperty("id").GetGuid() == id) return line;
        return null;
    }

    private static Task<HttpResponseMessage> ConfirmAsync(Member member, object body)
        => member.Client.PostAsJsonAsync($"{Base}/review/confirm", body);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(HttpStatusCode.OK == response.StatusCode, await response.Content.ReadAsStringAsync());
        return await JsonAsync(response);
    }

    /// <summary>
    /// Right before the run saves what it read of <paramref name="account"/> (the lines, and what the rules decided
    /// about them), <paramref name="meanwhile"/> happens, once. The counter says whether it did.
    /// </summary>
    private static StrongBox<int> BeforeTheRunSaves(OpenFinanceApiFactory factory, string account, Func<Task> meanwhile)
    {
        var happened = new StrongBox<int>(0);
        factory.Pluggy.BeforeAnswer = request =>
        {
            if (request.Path == "/transactions" && request.Query.Contains(account, StringComparison.Ordinal)
                && Interlocked.Exchange(ref happened.Value, 1) == 0)
            {
                factory.BeforeNextSave = meanwhile;
            }

            return Task.CompletedTask;
        };
        return happened;
    }

    /// <summary>Ana with a manual expense of R$ 25,00 and the same purchase arriving from the bank the next day.</summary>
    private static async Task<(Member Ana, Guid Connection, Guid Existing, Guid Line)> WithDuplicateAsync(
        OpenFinanceApiFactory factory, string? merchant = null, string category = "OUTROS")
    {
        var (ana, connection) = await AnaAsync(factory);
        var existing = await ManualAsync(ana, 25.00m, Noon(), "Padaria", merchant, category);
        Bank(factory, Checking, new FakeTransaction("t26-padaria", Noon(1), -25.00m)
        {
            Description = "Padaria Exemplo",
            MerchantName = "Padaria Exemplo Ltda",
            Category = "Groceries",
            CategoryId = "10000000",
        });
        await DoneAsync(factory, ana, connection);
        return (ana, connection, existing, await LineIdAsync(factory, "t26-padaria"));
    }

    // ================================================================ rule 4: suspected duplicate

    [Fact]
    public async Task AManualExpenseOf25_AndTheBankLineOfTheNextDay_AreASuspectedDuplicate_ThreeDaysLaterTheyAreNot_AndAnotherGroupNeverCounts()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var carla = await factory.RegisterAsync("Carla Modelo"); // another group
        var padaria = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        await ManualAsync(ana, 40.00m, Noon(), "Mercado");
        await ManualAsync(carla, 77.00m, Noon(), "Farmácia de outro grupo");
        Bank(factory, Checking,
            Debit("t26-next-day", Noon(1), 25.00m),
            Debit("t26-three-days", Noon(3), 40.00m),
            Debit("t26-other-group", Noon(), 77.00m));

        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(padaria)), await StateAsync(factory, "t26-next-day"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-three-days"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-other-group"));
        Assert.Null((await MirrorRowAsync(factory, "t26-next-day"))["person_decided_at_utc"]);
    }

    [Fact]
    public async Task TheDayOfTheTransactionOfTheApp_IsTheDayInBrazil_NotTheDayInUtc()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        // 22:00 in Brasília: already the next day in UTC.
        var lateEvening = Today.AddDays(-10).AddDays(1).AddHours(1);
        var near = await ManualAsync(ana, 31.00m, lateEvening, "Jantar");
        await ManualAsync(ana, 32.00m, lateEvening, "Bar");
        Bank(factory, Checking,
            // The day before in Brazil (one day apart); two days apart if the days were counted in UTC.
            Debit("t26-brazil-near", Noon(-1), 31.00m),
            // Two days after in Brazil; only one if the days were counted in UTC.
            Debit("t26-brazil-far", Noon(2), 32.00m));

        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(near)), await StateAsync(factory, "t26-brazil-near"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-brazil-far"));
    }

    [Fact]
    public async Task ASuspectedDuplicate_IsOnlyAnExpenseInReais_AgainstATransactionInReais_ThatDidNotComeFromTheBank_AndOfTheSameBankWhenItIsKnown()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var inDollars = await ManualAsync(ana, 11.00m, Noon(), "Em dólar");
        var fromTheBank = await ManualAsync(ana, 12.00m, Noon(), "Já veio do banco");
        var otherBank = await ManualAsync(ana, 13.00m, Noon(), "Notificação de outro banco");
        var sameBank = await ManualAsync(ana, 14.00m, Noon(), "Notificação do mesmo banco");
        await ManualAsync(ana, 15.00m, Noon(), "Linha em dólar");
        await ManualAsync(ana, 16.00m, Noon(), "Entrada");
        var imported = await ManualAsync(ana, 17.00m, Noon(), "Importada do extrato");
        await factory.ExecuteAsync($"UPDATE transactions SET currency = 'USD' WHERE id = '{Sql(inDollars)}'");
        await factory.ExecuteAsync($"UPDATE transactions SET source = 3, bank = 'Banco Exemplo' WHERE id = '{Sql(fromTheBank)}'");
        await factory.ExecuteAsync($"UPDATE transactions SET source = 2, bank = 'NUBANK' WHERE id = '{Sql(otherBank)}'");
        await factory.ExecuteAsync($"UPDATE transactions SET source = 2, bank = 'ITAU' WHERE id = '{Sql(sameBank)}'");
        await factory.ExecuteAsync($"UPDATE transactions SET source = 1, bank = 'OCR Import' WHERE id = '{Sql(imported)}'");
        // The connection of this test is of a bank the app knows by name.
        factory.Pluggy.ConnectorNameOverride[FakePluggyServer.ItemWithAccounts] = "Itaú";
        Bank(factory, Checking,
            Debit("t26-usd-transaction", Noon(), 11.00m),
            Debit("t26-from-bank", Noon(), 12.00m),
            Debit("t26-other-bank", Noon(), 13.00m),
            Debit("t26-same-bank", Noon(), 14.00m),
            Debit("t26-usd-line", Noon(), 15.00m) with { CurrencyCode = "USD" },
            Credit("t26-credit", Noon(), 16.00m),
            Debit("t26-imported", Noon(), 17.00m));

        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-usd-transaction"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-from-bank"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-other-bank"));
        Assert.Equal(("Pending", null, Sql(sameBank)), await StateAsync(factory, "t26-same-bank"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-usd-line"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-credit"));
        Assert.Equal(("Pending", null, Sql(imported)), await StateAsync(factory, "t26-imported"));
    }

    /// <summary>A purchase as the notification capture of the app stores it: through its own route, with the bank it names.</summary>
    private static async Task<Guid> NotificationAsync(OpenFinanceApiFactory factory, Member member, string bank, decimal amount, DateTime whenUtc)
    {
        var sent = await member.Client.PostAsJsonAsync("/api/v1/integrations/events", new
        {
            bank,
            amount,
            currency = "BRL",
            eventTimestamp = whenUtc,
            description = "Compra aprovada",
            merchant = "Padaria Exemplo",
            rawNotificationText = "Compra aprovada na Padaria Exemplo",
        });
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        var row = Assert.Single(await factory.RowsAsync("SELECT id, source, bank FROM transactions"));
        Assert.Equal((2L, bank), (Convert.ToInt64(row["source"], System.Globalization.CultureInfo.InvariantCulture), (string)row["bank"]!));
        return Guid.Parse((string)row["id"]!);
    }

    [Theory]
    // Every connection made through "Meu Pluggy" may carry that name, or none: it says nothing about the bank.
    [InlineData("MeuPluggy", true)]
    [InlineData("", true)]
    [InlineData("Banco Exemplo", true)]
    // The same bank, as the connection spells it.
    [InlineData("Nubank", true)]
    [InlineData("Nu Pagamentos S.A.", true)]
    // Another bank the app knows: the purchase of the notification was not made in this account.
    [InlineData("Itaú", false)]
    public async Task APurchaseCapturedFromANubankNotification_IsSuspected_UnlessTheConnectionIsOfAnotherBankTheAppKnows(string connector, bool suspected)
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var captured = await NotificationAsync(factory, ana, "NUBANK", 25.00m, Noon());
        factory.Pluggy.ConnectorNameOverride[FakePluggyServer.ItemWithAccounts] = connector;
        Bank(factory, Checking, Debit("t26-notified", Noon(1), 25.00m));

        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, suspected ? Sql(captured) : null), await StateAsync(factory, "t26-notified"));
    }

    [Fact]
    public async Task AStatementLineImportedWithNoTime_IsComparedByTheDayWritten_NotByTheEveningBeforeInBrasilia()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        // As the statement import stores a line: source OcrImport, bank "OCR Import", the date at midnight UTC.
        async Task<Guid> ImportedAsync(decimal amount, int daysAfterTheBase)
        {
            var id = await ManualAsync(ana, amount, Today.AddDays(-10 + daysAfterTheBase), "Linha do extrato");
            await factory.ExecuteAsync($"UPDATE transactions SET source = 1, bank = 'OCR Import' WHERE id = '{Sql(id)}'");
            return id;
        }

        var dayBefore = await ImportedAsync(61.00m, -1);
        var sameDay = await ImportedAsync(62.00m, 0);
        var dayAfter = await ImportedAsync(63.00m, 1);
        await ImportedAsync(64.00m, -2);
        await ImportedAsync(65.00m, 2);
        Bank(factory, Checking,
            Debit("t26-statement-day-before", Noon(), 61.00m),
            Debit("t26-statement-same-day", Noon(), 62.00m),
            Debit("t26-statement-day-after", Noon(), 63.00m),
            Debit("t26-statement-two-before", Noon(), 64.00m),
            Debit("t26-statement-two-after", Noon(), 65.00m));

        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(dayBefore)), await StateAsync(factory, "t26-statement-day-before"));
        Assert.Equal(("Pending", null, Sql(sameDay)), await StateAsync(factory, "t26-statement-same-day"));
        Assert.Equal(("Pending", null, Sql(dayAfter)), await StateAsync(factory, "t26-statement-day-after"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-statement-two-before"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-statement-two-after"));

        // The review shows the day of the statement as written.
        var line = await LineIdAsync(factory, "t26-statement-day-before");
        var shown = Find((await ReviewAsync(ana, Noon())).GetProperty("expenses"), line)!.Value;
        Assert.Equal(
            Today.AddDays(-11).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            shown.GetProperty("match").GetProperty("day").GetString());
    }

    [Fact]
    public async Task OneTransactionOfTheApp_IsSuspectedByEveryBankLineThatLooksLikeIt_AlsoInALaterSynchronisation()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var coffee = await ManualAsync(ana, 9.00m, Noon(), "Café");
        // Two coffees of the same value on the same day at the bank, one of them typed by hand: nobody but the
        // person knows which. Both are marked, so that neither goes in "Selecionar tudo".
        Bank(factory, Checking, Debit("t26-coffee-1", Noon(), 9.00m), Debit("t26-coffee-2", Noon().AddHours(2), 9.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-1"));
        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-2"));

        // A third one arrives later: it is marked too, and the others keep their mark.
        factory.Pluggy.Transactions[Checking].Add(Debit("t26-coffee-3", Noon().AddHours(3), 9.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-3"));
        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-1"));
        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-2"));
    }

    [Fact]
    public async Task ATransactionAlreadyReconciledWithALine_IsNotSuspectedAgainByAnotherLine()
    {
        await using var factory = NewFactory();
        var (ana, connection, existing, line) = await WithDuplicateAsync(factory);
        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }));

        factory.Pluggy.Transactions[Checking].Add(Debit("t26-padaria-again", Noon(1).AddHours(1), 25.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-padaria-again"));
        Assert.Equal(Sql(existing), (await MirrorRowAsync(factory, "t26-padaria"))["linked_transaction_id"]);
    }

    [Fact]
    public async Task DeletingTheTransaction_ClearsTheSuspicion_AndTheLineIsAnOrdinaryExpenseAgain()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{existing}")).StatusCode);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-padaria"));
        var review = await ReviewAsync(ana, Noon(1));
        Assert.Equal(JsonValueKind.Null, Find(review.GetProperty("expenses"), line)!.Value.GetProperty("match").ValueKind);
        Assert.Equal(0, review.GetProperty("suspectedDuplicates").GetInt32());
    }

    // ================================================================ rule 1: transfer between accounts of the group

    [Fact]
    public async Task ADebitAndACreditOfTheSameValueOnTheSameDay_InAccountsOfTwoPeopleOfTheGroup_AreBothIgnoredAsTransfer()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, Checking, Debit("t26-sent", Noon(), 500.00m, "PIX ENVIADO"), Debit("t26-market", Noon(), 80.00m, "Mercado Exemplo"));
        Bank(factory, OtherChecking, Credit("t26-received", Noon().AddHours(1), 500.00m, "PIX RECEBIDO"));

        // Ana's side arrives first: nothing to pair it with yet.
        await DoneAsync(factory, ana, anaConnection);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-sent"));

        // The other leg arrives with Bruno's synchronisation: both are ignored, the one that was waiting too.
        await DoneAsync(factory, bruno, brunoConnection);

        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-sent"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-received"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-market"));
        Assert.NotNull((await MirrorRowAsync(factory, "t26-sent"))["reviewed_at_utc"]);
        Assert.Null((await MirrorRowAsync(factory, "t26-sent"))["person_decided_at_utc"]);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
    }

    [Fact]
    public async Task InAccountsOfTwoDifferentGroups_NothingIsIgnored()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (carla, carlaConnection) = await WithOtherBankAsync(factory, "Carla Modelo", joinCode: null);
        Bank(factory, Checking,
            Debit("t26-sent", Noon(), 500.00m, "PIX ENVIADO"),
            // Who holds an account in ANOTHER group is nobody of this one.
            Credit("t26-from-carla", Noon(), 310.00m) with { PayerName = "CARLA MODELO" });
        Bank(factory, OtherChecking, Credit("t26-received", Noon(), 500.00m, "PIX RECEBIDO"));

        await DoneAsync(factory, carla, carlaConnection);
        await DoneAsync(factory, ana, anaConnection);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-sent"));
        // The other order of arrival.
        Bank(factory, Checking, Debit("t26-sent-2", Noon(1), 600.00m, "PIX ENVIADO"));
        Bank(factory, OtherChecking, Credit("t26-received-2", Noon(1), 600.00m, "PIX RECEBIDO"));
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, carla, carlaConnection);

        foreach (var id in new[] { "t26-sent", "t26-received", "t26-from-carla", "t26-sent-2", "t26-received-2" })
            Assert.Equal(("Pending", null, null), await StateAsync(factory, id));
    }

    [Fact]
    public async Task BetweenTwoAccountsOfTheSamePerson_TheTransferIsIgnoredToo_AndItComesBeforeTheBillPaymentRule()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        // The payment of the bill seen from both sides: it leaves the checking account and enters the card.
        Bank(factory, Checking, Debit("t26-bill-out", Noon(), 1200.00m, "PAGAMENTO DE FATURA"));
        Bank(factory, Card, Credit("t26-bill-in", Noon(), 1200.00m, "Pagamento recebido"));

        await DoneAsync(factory, ana, connection);

        // The checking account is read first: alone, its line was a bill payment; with the credit on the card, a transfer.
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-bill-out"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-bill-in"));

        // The other order of arrival: the credit on the card one day, the debit on a later synchronisation.
        factory.Pluggy.Transactions[Card].Add(Credit("t26-bill-in-2", Noon(1), 1300.00m, "Pagamento recebido"));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-bill-in-2"));
        factory.Pluggy.Transactions[Checking].Add(Debit("t26-bill-out-2", Noon(1), 1300.00m, "PAGAMENTO DE FATURA"));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-bill-out-2"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-bill-in-2"));
        // What was paired stays paired: a third line of the same value pairs with nothing.
        factory.Pluggy.Transactions[Card].Add(Credit("t26-bill-in-3", Noon(1).AddHours(1), 1300.00m, "Pagamento recebido"));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-bill-in-3"));
    }

    [Fact]
    public async Task ABillPaymentThePersonRestored_IsNotPairedWithTheCreditThatArrivesLater()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, Debit("t26-bill-out", Noon(), 1200.00m, "PAGAMENTO DE FATURA"));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-bill-out"));
        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { await LineIdAsync(factory, "t26-bill-out") }));

        Bank(factory, Card, Credit("t26-bill-in", Noon(), 1200.00m, "Pagamento recebido"));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-bill-out"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-bill-in"));
    }

    [Fact]
    public async Task ATransfer_NeedsTheSameDay_TwoAccounts_OppositeWays_ReaisAndAValue_AndBothSettledAtTheBank()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, Checking,
            Debit("t26-other-day", Noon(), 101.00m),
            // the same account: a purchase and its refund
            Debit("t26-same-account-out", Noon(), 102.00m),
            Credit("t26-same-account-in", Noon(), 102.00m),
            Debit("t26-both-out", Noon(), 103.00m),
            Debit("t26-usd-out", Noon(), 104.00m) with { CurrencyCode = "USD" },
            new FakeTransaction("t26-zero-out", Noon(), 0m) { Type = "DEBIT", Description = "Tarifa zerada" },
            Debit("t26-bank-pending-out", Noon(), 106.00m) with { Status = "PENDING" },
            Debit("t26-other-pending-out", Noon(), 107.00m));
        Bank(factory, OtherChecking,
            Credit("t26-other-day-in", Noon(1), 101.00m),
            Debit("t26-both-out-2", Noon(), 103.00m),
            Credit("t26-usd-in", Noon(), 104.00m) with { CurrencyCode = "USD" },
            new FakeTransaction("t26-zero-in", Noon(), 0m) { Type = "CREDIT", Description = "Crédito zerado" },
            Credit("t26-bank-pending-in", Noon(), 106.00m),
            Credit("t26-other-pending-in", Noon(), 107.00m) with { Status = "PENDING" });

        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);
        await DoneAsync(factory, ana, anaConnection);

        foreach (var row in await MirrorAsync(factory))
            Assert.True(("Pending", null) == ((string)row["review_state"]!, (string?)row["auto_reason"]), $"{row["pluggy_transaction_id"]} was {row["review_state"]}");
    }

    [Fact]
    public async Task EachLegPairsWithOneOtherLegOnly()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, Checking,
            Debit("t26-out-1", Noon(), 50.00m), Debit("t26-out-2", Noon().AddHours(1), 50.00m),
            Debit("t26-single-out", Noon(), 60.00m));
        Bank(factory, OtherChecking,
            Credit("t26-in", Noon().AddHours(2), 50.00m),
            Credit("t26-in-a", Noon().AddHours(1), 60.00m), Credit("t26-in-b", Noon().AddHours(2), 60.00m));

        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);

        // Two legs waiting and one arriving: the oldest one waiting takes the pair; the other stays for the review.
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-in"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-out-1"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-out-2"));
        // One leg waiting and two arriving in the same run: the first to arrive takes it; the second pairs with nothing.
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-single-out"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-in-a"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-in-b"));
    }

    [Fact]
    public async Task ALegThatWasPendingAtTheBank_IsPairedWhenTheBankSettlesIt()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, Checking, Debit("t26-out", Noon(), 60.00m) with { Status = "PENDING" });
        Bank(factory, OtherChecking, Credit("t26-in", Noon(), 60.00m));
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-out"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-in"));

        Bank(factory, Checking, Debit("t26-out", Noon(), 60.00m));
        await DoneAsync(factory, ana, anaConnection);

        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-out"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-in"));
    }

    [Fact]
    public async Task ALineReadAgainWithoutAnythingNew_IsNotJudgedAgain()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, Debit("t26-padaria", Noon(1), 25.00m));
        await DoneAsync(factory, ana, connection);
        // Typed by hand only after the line arrived: the rules ran when the line came in.
        await ManualAsync(ana, 25.00m, Noon(), "Padaria");

        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-padaria"));
    }

    // ================================================================ rule 2: bill payment

    [Fact]
    public async Task ADebitDescribedAsPagamentoDeFatura_InACheckingAccount_IsIgnoredAsBillPayment()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking,
            new FakeTransaction("t26-raw", Noon(), -900.00m) { Description = "Pagamento efetuado", DescriptionRaw = "PAGAMENTO DE FATURA" },
            // some banks only fill the description
            new FakeTransaction("t26-description", Noon(), -910.00m) { Description = "Pagamento de fatura cartão exemplo" },
            new FakeTransaction("t26-accents", Noon(), -920.00m) { DescriptionRaw = "PAGAMENTO FATURA CARTÃO" },
            Debit("t26-boleto", Noon(), 930.00m, "PAGAMENTO DE BOLETO"),
            new FakeTransaction("t26-usd", Noon(), -940.00m) { DescriptionRaw = "PAGAMENTO DE FATURA", CurrencyCode = "USD" },
            new FakeTransaction("t26-zero", Noon(), 0m) { Type = "DEBIT", DescriptionRaw = "PAGAMENTO DE FATURA" },
            new FakeTransaction("t26-refund", Noon(), 950.00m) { Type = "CREDIT", DescriptionRaw = "ESTORNO PAGAMENTO DE FATURA" });
        // On the card itself the words describe a purchase or the credit of the payment: never this rule.
        Bank(factory, Card, new FakeTransaction("t26-on-card", Noon(), 960.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" });

        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-raw"));
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-description"));
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-accents"));
        foreach (var kept in new[] { "t26-boleto", "t26-usd", "t26-zero", "t26-refund", "t26-on-card" })
            Assert.Equal(("Pending", null, null), await StateAsync(factory, kept));
    }

    [Fact]
    public async Task ABillPaymentStillPendingAtTheBank_Waits_AndIsIgnoredWhenTheBankSettlesIt()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA", Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-bill"));

        Bank(factory, Checking, new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" });
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-bill"));
    }

    [Fact]
    public async Task ABillPayment_ComesBeforeTheSuspectedDuplicate()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        await ManualAsync(ana, 900.00m, Noon(), "Fatura do cartão");
        Bank(factory, Checking, new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" });

        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-bill"));
    }

    // ================================================================ rule 3: credit from someone of the group

    [Fact]
    public async Task ACreditPaidByWhoHoldsAnotherAccountOfTheGroup_IsIgnoredAsTransfer_AnyoneElseIsNot()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        await factory.RegisterAsync("Dora Modelo Lima", ana.JoinCode); // of the group, but holds no account
        Bank(factory, Checking,
            Credit("t26-from-bruno", Noon(), 300.00m) with { PayerName = "BRUNO EXEMPLO SILVA" },
            Credit("t26-from-company", Noon(), 310.00m) with { PayerName = "EMPRESA EXEMPLO LTDA" },
            Credit("t26-from-dora", Noon(), 320.00m) with { PayerName = "DORA MODELO LIMA" },
            Credit("t26-no-payer", Noon(), 330.00m),
            Credit("t26-usd", Noon(), 340.00m) with { PayerName = "BRUNO EXEMPLO SILVA", CurrencyCode = "USD" },
            Credit("t26-bank-pending", Noon(), 350.00m) with { PayerName = "BRUNO EXEMPLO SILVA", Status = "PENDING" },
            // a debit naming Bruno is money that left, not a credit
            Debit("t26-debit", Noon(), 360.00m) with { PayerName = "BRUNO EXEMPLO SILVA" },
            // Ana herself, from a bank she did not connect: she holds another account of the group (the card)
            Credit("t26-from-ana", Noon(), 370.00m) with { PayerName = "ANA EXEMPLO DE SOUZA" });
        await DoneAsync(factory, bruno, brunoConnection);

        await DoneAsync(factory, ana, anaConnection);

        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-from-bruno"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-from-ana"));
        foreach (var kept in new[] { "t26-from-company", "t26-from-dora", "t26-no-payer", "t26-usd", "t26-bank-pending", "t26-debit" })
            Assert.Equal(("Pending", null, null), await StateAsync(factory, kept));
    }

    [Fact]
    public async Task TheOnlyAccountOfTheGroup_HasNoOtherHolder_ACreditFromItsOwnHolderStaysForTheReview()
    {
        await using var factory = NewFactory();
        var (bruno, connection) = await WithOtherBankAsync(factory, "Bruno Silva", joinCode: null);
        Bank(factory, OtherChecking, Credit("t26-own", Noon(), 300.00m) with { PayerName = "BRUNO EXEMPLO SILVA" });

        await DoneAsync(factory, bruno, connection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-own"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenWhoHeldTheOtherAccountLeavesTheGroup_TheirLegGoesAway_TheOtherStaysIgnored_AndTheirNameNoLongerIgnoresAnything(bool removedByTheOwner)
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, Checking, Debit("t26-sent", Noon(), 500.00m));
        Bank(factory, OtherChecking, Credit("t26-received", Noon(), 500.00m));
        await DoneAsync(factory, bruno, brunoConnection);
        await DoneAsync(factory, ana, anaConnection);
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-sent"));

        var gone = removedByTheOwner
            ? await ana.Client.DeleteAsync($"/api/v1/couples/members/{bruno.UserId}")
            : await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
        Assert.True(gone.IsSuccessStatusCode, await gone.Content.ReadAsStringAsync());

        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_transactions WHERE pluggy_transaction_id = 't26-received'"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-sent"));

        // New lines of Ana: nothing of Bruno is left to pair with, and his name holds no account of the group any more.
        factory.Pluggy.Transactions[Checking].Add(Credit("t26-from-bruno", Noon(1), 300.00m) with { PayerName = "BRUNO EXEMPLO SILVA" });
        factory.Pluggy.Transactions[Checking].Add(Debit("t26-sent-again", Noon(1), 500.00m));
        await DoneAsync(factory, ana, anaConnection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-from-bruno"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-sent-again"));
        // It can still be brought back by who stayed.
        var restored = await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { await LineIdAsync(factory, "t26-sent") }));
        Assert.Single(restored.GetProperty("restored").EnumerateArray());
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-sent"));
    }

    // ================================================================ the decision of the person prevails

    [Fact]
    public async Task RestoringAnIgnoredLine_BringsItBackToTheReview_AndNoLaterSynchronisationIgnoresItAgain()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, Checking,
            Debit("t26-sent", Noon(), 500.00m, "PIX ENVIADO"),
            new FakeTransaction("t26-bill", Noon(), -900.00m) { Description = "Pagamento de fatura", DescriptionRaw = "PAGAMENTO DE FATURA" });
        Bank(factory, OtherChecking, Credit("t26-received", Noon(), 500.00m, "PIX RECEBIDO"));
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-sent"));
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-bill"));
        var sent = await LineIdAsync(factory, "t26-sent");
        var bill = await LineIdAsync(factory, "t26-bill");

        // Anyone of the group restores.
        var restored = await OkAsync(bruno.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { sent, bill }));

        Assert.Equal(new[] { sent, bill }.Order(), restored.GetProperty("restored").EnumerateArray().Select(e => e.GetGuid()).Order());
        foreach (var id in new[] { "t26-sent", "t26-bill" })
        {
            var row = await MirrorRowAsync(factory, id);
            Assert.Equal(("Pending", null), ((string)row["review_state"]!, (string?)row["auto_reason"]));
            Assert.Null(row["reviewed_at_utc"]);
            Assert.NotNull(row["person_decided_at_utc"]);
        }

        var review = await ReviewAsync(ana, Noon());
        Assert.NotNull(Find(review.GetProperty("expenses"), sent));
        Assert.NotNull(Find(review.GetProperty("expenses"), bill));
        Assert.Null(Find(review.GetProperty("ignored"), sent));

        // Both banks are read again, and a second credit of the same value arrives at Bruno's: nothing touches what was restored.
        factory.Pluggy.Transactions[OtherChecking].Add(Credit("t26-received-2", Noon().AddHours(3), 500.00m, "PIX RECEBIDO"));
        await ManualAsync(ana, 900.00m, Noon(), "Fatura digitada depois");
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);
        await DoneAsync(factory, ana, anaConnection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-sent"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-bill"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-received-2"));
        // The leg nobody restored stays as it was.
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-received"));
    }

    [Fact]
    public async Task RestoringAnIgnoredEntry_BringsItBackToWaiting_AndADebitOfTheSameValueThatArrivesLater_DoesNotPairWithIt()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, Checking,
            Debit("t26-sent", Noon(), 500.00m, "PIX ENVIADO"),
            // a credit ignored by the rule of the partner: Bruno holds another account of the group
            Credit("t26-from-bruno", Noon(), 300.00m) with { PayerName = "BRUNO EXEMPLO SILVA" });
        Bank(factory, OtherChecking, Credit("t26-received", Noon().AddHours(1), 500.00m, "PIX RECEBIDO"));
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-received"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-from-bruno"));
        var received = await LineIdAsync(factory, "t26-received");
        var fromBruno = await LineIdAsync(factory, "t26-from-bruno");
        var review = await ReviewAsync(ana, Noon());
        Assert.NotNull(Find(review.GetProperty("ignored"), received));
        Assert.NotNull(Find(review.GetProperty("ignored"), fromBruno));

        // Only the entries are restored (the debit leg stays ignored).
        var restored = await OkAsync(bruno.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { received, fromBruno }));

        Assert.Equal(new[] { received, fromBruno }.Order(), restored.GetProperty("restored").EnumerateArray().Select(e => e.GetGuid()).Order());
        foreach (var id in new[] { "t26-received", "t26-from-bruno" })
        {
            var row = await MirrorRowAsync(factory, id);
            Assert.Equal(("Pending", null), ((string)row["review_state"]!, (string?)row["auto_reason"]));
            Assert.Null(row["reviewed_at_utc"]);
            Assert.NotNull(row["person_decided_at_utc"]);
        }

        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-sent"));
        review = await ReviewAsync(ana, Noon());
        Assert.Null(Find(review.GetProperty("ignored"), received));
        Assert.Null(Find(review.GetProperty("ignored"), fromBruno));
        Assert.NotNull(Find(review.GetProperty("ignored"), await LineIdAsync(factory, "t26-sent")));

        // Debits of the same values arrive later, in the other account of the group: the restored entries are not legs to pair with.
        Bank(factory, Checking,
            Debit("t26-sent", Noon(), 500.00m, "PIX ENVIADO"),
            Credit("t26-from-bruno", Noon(), 300.00m) with { PayerName = "BRUNO EXEMPLO SILVA" },
            Debit("t26-sent-2", Noon().AddHours(2), 500.00m, "PIX ENVIADO"));
        Bank(factory, OtherChecking,
            Credit("t26-received", Noon().AddHours(1), 500.00m, "PIX RECEBIDO"),
            Debit("t26-sent-3", Noon().AddHours(2), 300.00m, "PIX ENVIADO"));
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);
        await DoneAsync(factory, ana, anaConnection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-received"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-from-bruno"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-sent-2"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-sent-3"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-sent"));
    }

    [Fact]
    public async Task TheIgnoredOfAMonth_AreOnlyThoseOfThatMonth()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        // 35 days apart: never in the same month.
        var older = Noon(-35);
        Bank(factory, Checking,
            new FakeTransaction("t26-bill-now", Noon(), -900.00m) { Description = "Pagamento de fatura", DescriptionRaw = "PAGAMENTO DE FATURA" },
            new FakeTransaction("t26-bill-older", older, -800.00m) { Description = "Pagamento de fatura", DescriptionRaw = "PAGAMENTO DE FATURA" });
        await DoneAsync(factory, ana, connection);
        var now = await LineIdAsync(factory, "t26-bill-now");
        var before = await LineIdAsync(factory, "t26-bill-older");
        Assert.NotEqual(BrazilTime.MonthOf(Noon()), BrazilTime.MonthOf(older));

        var thisMonth = (await ReviewAsync(ana, Noon())).GetProperty("ignored").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();
        var olderMonth = (await ReviewAsync(ana, older)).GetProperty("ignored").EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(new[] { now }, thisMonth);
        Assert.Equal(new[] { before }, olderMonth);
    }

    [Fact]
    public async Task ALineThePersonDiscardedAndRestored_IsNeverIgnoredNorSuspectedByALaterSynchronisation()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, Debit("t26-out", Noon(), 70.00m) with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        var line = await LineIdAsync(factory, "t26-out");
        await OkAsync(ConfirmAsync(ana, new { discard = new[] { line } }));
        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { line }));
        await ManualAsync(ana, 70.00m, Noon(), "Digitada");
        // Its other leg arrives on the card and waits (the line of the person is not one to pair with)...
        Bank(factory, Card, Credit("t26-in", Noon(), 70.00m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-in"));

        // ...then the bank settles the line of the person: it would be judged now, with a leg waiting and a transaction
        // of the same value typed by hand. Nothing touches it, nor the leg that would have been paired with it.
        Bank(factory, Checking, Debit("t26-out", Noon(), 70.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-out"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-in"));
        Assert.Equal("Posted", (await MirrorRowAsync(factory, "t26-out"))["status"]);
    }

    [Fact]
    public async Task ALegSomeoneDiscardsWhileTheRunIsPairingIt_StaysDiscarded_AndTheRunEndsWell()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, Checking, Debit("t26-sent", Noon(), 500.00m));
        Bank(factory, OtherChecking, Credit("t26-received", Noon(), 500.00m));
        await DoneAsync(factory, ana, anaConnection);
        var sent = await LineIdAsync(factory, "t26-sent");
        var once = 0;
        factory.Pluggy.BeforeAnswer = request =>
        {
            // Bruno's run has the transactions of the account: its next save is the one that would ignore both legs.
            if (request.Path == "/transactions" && request.Query.Contains(OtherChecking, StringComparison.Ordinal)
                && Interlocked.Exchange(ref once, 1) == 0)
            {
                factory.BeforeNextSave = async () =>
                {
                    var discarded = await ConfirmAsync(ana, new { discard = new[] { sent } });
                    Assert.True(HttpStatusCode.OK == discarded.StatusCode, await discarded.Content.ReadAsStringAsync());
                };
            }

            return Task.CompletedTask;
        };

        await DoneAsync(factory, bruno, brunoConnection);

        Assert.Equal(1, once);
        // The review of the person is kept whole: the run did not write its decision over it.
        Assert.Equal(("Discarded", null, null), await StateAsync(factory, "t26-sent"));
        // And the leg that arrived is not half a transfer: with nothing left to pair with, it waits like any other line.
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-received"));
    }

    // ================================================================ GET review

    [Fact]
    public async Task Review_ShowsTheSuspectedDuplicateWithTheTransactionItLooksLike_AndTheIgnoredOnesApartWithTheReason()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        var existing = await ManualAsync(ana, 25.00m, Noon(), "Padaria", merchant: "Padoca");
        Bank(factory, Checking,
            Debit("t26-padaria", Noon(), 25.00m, "Padaria Exemplo"),
            Debit("t26-market", Noon(), 80.00m, "Mercado Exemplo"),
            Debit("t26-sent", Noon(), 500.00m, "PIX ENVIADO"),
            new FakeTransaction("t26-bill", Noon(), -900.00m) { Description = "Pagamento de fatura", DescriptionRaw = "PAGAMENTO DE FATURA" });
        Bank(factory, OtherChecking, Credit("t26-received", Noon(), 500.00m, "PIX RECEBIDO") with { PayerName = "PAGADORA INVENTADA MARCA" });
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);
        var padaria = await LineIdAsync(factory, "t26-padaria");
        var market = await LineIdAsync(factory, "t26-market");
        var sent = await LineIdAsync(factory, "t26-sent");
        var bill = await LineIdAsync(factory, "t26-bill");
        var received = await LineIdAsync(factory, "t26-received");

        // Anyone of the group sees the same.
        var review = await ReviewAsync(bruno, Noon());

        var expenses = review.GetProperty("expenses");
        Assert.Equal(new[] { padaria, market }.Order(), expenses.EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).Order());
        var duplicate = Find(expenses, padaria)!.Value;
        Assert.Equal("Debit", duplicate.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, duplicate.GetProperty("autoReason").ValueKind);
        var match = duplicate.GetProperty("match");
        Assert.Equal(existing, match.GetProperty("transactionId").GetGuid());
        Assert.Equal("Manual", match.GetProperty("source").GetString());
        Assert.Equal("Padaria", match.GetProperty("description").GetString());
        Assert.Equal("Padoca", match.GetProperty("merchant").GetString());
        Assert.Equal(25.00m, match.GetProperty("amount").GetDecimal());
        Assert.Equal("BRL", match.GetProperty("currency").GetString());
        Assert.Equal(Day(BrazilTime.ToLocal(Noon())), match.GetProperty("day").GetString());
        Assert.Equal(JsonValueKind.Null, Find(expenses, market)!.Value.GetProperty("match").ValueKind);
        Assert.Equal(1, review.GetProperty("suspectedDuplicates").GetInt32());

        var ignored = review.GetProperty("ignored");
        Assert.Equal(new[] { sent, bill, received }.Order(), ignored.EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).Order());
        Assert.Equal(("Transfer", "Debit", 500.00m), Shown(Find(ignored, sent)!.Value));
        Assert.Equal(("BillPayment", "Debit", 900.00m), Shown(Find(ignored, bill)!.Value));
        Assert.Equal(("Transfer", "Credit", 500.00m), Shown(Find(ignored, received)!.Value));
        Assert.Equal("Banco Modelo", Find(ignored, received)!.Value.GetProperty("bankName").GetString());
        Assert.Equal(JsonValueKind.Null, Find(ignored, sent)!.Value.GetProperty("match").ValueKind);

        // What was ignored is not counted nor added as something waiting.
        Assert.Equal(105.00m, review.GetProperty("pendingTotalBrl").GetDecimal());
        Assert.Equal(2, review.GetProperty("pendingAllMonths").GetInt32());
        Assert.Empty(review.GetProperty("discarded").EnumerateArray());
        // Never the raw JSON, nor the name of who paid (the bank sent one with the credit, and the mirror keeps it).
        Assert.DoesNotContain(FakePluggyServer.RawOnlyMarker, review.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("PAGADORA INVENTADA", (string)(await MirrorRowAsync(factory, "t26-received"))["raw_json"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("PAGADORA", review.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INVENTADA", review.GetRawText(), StringComparison.OrdinalIgnoreCase);

        static (string?, string?, decimal) Shown(JsonElement line)
            => (line.GetProperty("autoReason").GetString(), line.GetProperty("type").GetString(), line.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Review_OfAnotherGroup_ShowsNoneOfTheSuspicionsNorOfTheIgnored()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var carla = await factory.RegisterAsync("Carla Modelo");
        await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        Bank(factory, Checking,
            Debit("t26-padaria", Noon(), 25.00m),
            new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" });
        await DoneAsync(factory, ana, connection);

        var review = await ReviewAsync(carla, Noon());

        Assert.Empty(review.GetProperty("expenses").EnumerateArray());
        Assert.Empty(review.GetProperty("ignored").EnumerateArray());
        Assert.Equal(0, review.GetProperty("suspectedDuplicates").GetInt32());
    }

    [Fact]
    public async Task ASuspicionPointingToATransactionOfAnotherGroup_IsNeverShown_NorReconciled_NorFilledIn()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var carla = await factory.RegisterAsync("Carla Modelo");
        var ofCarla = await ManualAsync(carla, 25.00m, Noon(), "Segredo de outro grupo");
        Bank(factory, Checking, new FakeTransaction("t26-padaria", Noon(), -25.00m) { Description = "Padaria Exemplo", MerchantName = "Padaria Exemplo Ltda" });
        await DoneAsync(factory, ana, connection);
        var line = await LineIdAsync(factory, "t26-padaria");
        // Nothing of the app writes this; forced, as corrupted data would be.
        await factory.ExecuteAsync($"UPDATE bank_transactions SET matched_transaction_id = '{Sql(ofCarla)}' WHERE id = '{Sql(line)}'");

        var review = await ReviewAsync(ana, Noon());

        Assert.Equal(JsonValueKind.Null, Find(review.GetProperty("expenses"), line)!.Value.GetProperty("match").ValueKind);
        Assert.Equal(0, review.GetProperty("suspectedDuplicates").GetInt32());
        Assert.DoesNotContain("Segredo de outro grupo", review.GetRawText(), StringComparison.Ordinal);

        await AssertErrorAsync(
            await ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }),
            HttpStatusCode.Conflict, "BANK_TRANSACTION_NOT_MATCHED");
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t26-padaria"))["review_state"]);
        Assert.Null((await MirrorRowAsync(factory, "t26-padaria"))["linked_transaction_id"]);
        Assert.Null(Assert.Single(await factory.RowsAsync($"SELECT merchant FROM transactions WHERE id = '{Sql(ofCarla)}'"))["merchant"]);
    }

    // ================================================================ "É a mesma" (reconcile)

    [Theory]
    [InlineData(null, "OUTROS", "Padaria Exemplo Ltda", "ALIMENTACAO")]
    [InlineData("", "OUTROS", "Padaria Exemplo Ltda", "ALIMENTACAO")]
    [InlineData("Padoca", "OUTROS", "Padoca", "ALIMENTACAO")]
    [InlineData(null, "LAZER", "Padaria Exemplo Ltda", "LAZER")]
    [InlineData("Padoca", "LAZER", "Padoca", "LAZER")]
    public async Task ItIsTheSame_CreatesNoTransaction_MarksTheLineReconciled_AndFillsInTheExistingOneOnlyWhatWasEmptyOrOutros(
        string? merchant, string category, string expectedMerchant, string expectedCategory)
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory, merchant, category);
        var before = Assert.Single(await factory.RowsAsync("SELECT * FROM transactions"));

        var body = await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }));

        var reconciled = Assert.Single(body.GetProperty("reconciled").EnumerateArray());
        Assert.Equal((line, existing), (reconciled.GetProperty("id").GetGuid(), reconciled.GetProperty("transactionId").GetGuid()));
        Assert.Empty(body.GetProperty("created").EnumerateArray());
        Assert.Empty(body.GetProperty("discarded").EnumerateArray());
        Assert.Equal(0, body.GetProperty("alreadyConfirmed").GetInt32());

        var after = Assert.Single(await factory.RowsAsync("SELECT * FROM transactions"));
        Assert.Single(await factory.RowsAsync("SELECT id FROM transaction_event_ingests"));
        Assert.Equal(Sql(existing), after["id"]);
        Assert.Equal(expectedMerchant, after["merchant"]);
        Assert.Equal(expectedCategory, after["category"]);
        // Nothing else of the transaction of the person changes: not the description, the value, the date, the origin.
        foreach (var column in new[] { "description", "amount", "currency", "event_timestamp_utc", "bank", "source", "fingerprint", "user_id" })
            Assert.Equal(before[column], after[column]);

        var row = await MirrorRowAsync(factory, "t26-padaria");
        Assert.Equal("Reconciled", row["review_state"]);
        Assert.Equal(Sql(existing), row["linked_transaction_id"]);
        Assert.Null(row["matched_transaction_id"]);
        Assert.NotNull(row["reviewed_at_utc"]);

        // It leaves the review, and is not counted as something waiting.
        var review = await ReviewAsync(ana, Noon(1));
        Assert.Empty(review.GetProperty("expenses").EnumerateArray());
        Assert.Equal(0, review.GetProperty("pendingAllMonths").GetInt32());
        Assert.Equal(0, review.GetProperty("suspectedDuplicates").GetInt32());
    }

    [Fact]
    public async Task ItIsTheSame_SentTwice_AnswersTheSame_AndChangesNothingTheSecondTime()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);
        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }));
        // The person fixes the merchant by hand afterwards: a repeated request must not fill it again.
        await factory.ExecuteAsync($"UPDATE transactions SET merchant = NULL WHERE id = '{Sql(existing)}'");

        var again = await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }));

        var reconciled = Assert.Single(again.GetProperty("reconciled").EnumerateArray());
        Assert.Equal((line, existing), (reconciled.GetProperty("id").GetGuid(), reconciled.GetProperty("transactionId").GetGuid()));
        Assert.Null(Assert.Single(await factory.RowsAsync("SELECT merchant FROM transactions"))["merchant"]);
        Assert.Equal("Reconciled", (await MirrorRowAsync(factory, "t26-padaria"))["review_state"]);
    }

    [Fact]
    public async Task ItIsTheSame_IsForAnyoneOfTheGroup_AndNeverForAnotherGroup()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);
        var carla = await factory.RegisterAsync("Carla Modelo");
        var bruno = await factory.RegisterAsync("Bruno Silva", ana.JoinCode);

        await AssertErrorAsync(
            await ConfirmAsync(carla, new { reconcile = new[] { new { id = line } } }),
            HttpStatusCode.NotFound, "BANK_TRANSACTION_NOT_FOUND", "Lançamento do banco não encontrado.");
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-padaria"));
        Assert.Null(Assert.Single(await factory.RowsAsync("SELECT merchant FROM transactions"))["merchant"]);

        await OkAsync(ConfirmAsync(bruno, new { reconcile = new[] { new { id = line } } }));
        Assert.Equal("Reconciled", (await MirrorRowAsync(factory, "t26-padaria"))["review_state"]);
        // The transaction stays of who registered it.
        Assert.Equal(Sql(ana.UserId), Assert.Single(await factory.RowsAsync("SELECT user_id FROM transactions"))["user_id"]);
    }

    [Fact]
    public async Task ItIsTheSame_OfALineWithoutASuspicion_Answers409_AndChangesNothing()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, Debit("t26-market", Noon(), 80.00m));
        await DoneAsync(factory, ana, connection);
        var line = await LineIdAsync(factory, "t26-market");

        var refused = await ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } });

        await AssertErrorAsync(
            refused, HttpStatusCode.Conflict, "BANK_TRANSACTION_NOT_MATCHED",
            "Este lançamento não tem mais uma transação parecida para conciliar. Atualize a revisão e tente de novo.");
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-market"));
    }

    [Fact]
    public async Task ItIsTheSame_OfALineStillPendingAtTheBank_Answers422_AndNothingOfTheRequestIsStored()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var existing = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        await ManualAsync(ana, 80.00m, Noon(), "Mercado");
        Bank(factory, Checking, Debit("t26-padaria", Noon(), 25.00m) with { Status = "PENDING" }, Debit("t26-market", Noon(), 80.00m));
        await DoneAsync(factory, ana, connection);
        // A line the bank has not settled is suspected all the same: the person sees it coming.
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-padaria"));
        var pendingAtBank = await LineIdAsync(factory, "t26-padaria");
        var market = await LineIdAsync(factory, "t26-market");

        var refused = await ConfirmAsync(ana, new { reconcile = new[] { new { id = market }, new { id = pendingAtBank } } });

        await AssertErrorAsync(refused, HttpStatusCode.UnprocessableEntity, "TRANSACTION_NOT_POSTED");
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t26-market"))["review_state"]);
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t26-padaria"))["review_state"]);
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("discarded")]
    [InlineData("ignored")]
    public async Task ItIsTheSame_OfALineAlreadyDecided_Answers409NotPending_AndChangesNothing(string decided)
    {
        await using var factory = NewFactory();
        var (ana, _, _, line) = await WithDuplicateAsync(factory);
        var state = decided switch
        {
            "confirmed" => "Confirmed",
            "discarded" => "Discarded",
            _ => "Ignored",
        };
        if (decided == "confirmed") await OkAsync(ConfirmAsync(ana, new { asNew = new[] { line } }));
        else if (decided == "discarded") await OkAsync(ConfirmAsync(ana, new { discard = new[] { line } }));
        else await factory.ExecuteAsync($"UPDATE bank_transactions SET review_state = 'Ignored', auto_reason = 'Transfer' WHERE id = '{Sql(line)}'");
        var transactions = (await factory.RowsAsync("SELECT id FROM transactions")).Count;

        var refused = await ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } });

        await AssertErrorAsync(refused, HttpStatusCode.Conflict, "BANK_TRANSACTION_NOT_PENDING", "Este lançamento já foi revisado. Atualize a revisão e tente de novo.");
        Assert.Equal(state, (await MirrorRowAsync(factory, "t26-padaria"))["review_state"]);
        Assert.Equal(transactions, (await factory.RowsAsync("SELECT id FROM transactions")).Count);
    }

    [Fact]
    public async Task AnIgnoredLine_CannotBeConfirmedNorDiscardedBeforeBeingRestored()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" });
        await DoneAsync(factory, ana, connection);
        var line = await LineIdAsync(factory, "t26-bill");

        foreach (var body in new object[] { new { expenses = new[] { new { id = line } } }, new { asNew = new[] { line } }, new { discard = new[] { line } } })
            await AssertErrorAsync(await ConfirmAsync(ana, body), HttpStatusCode.Conflict, "BANK_TRANSACTION_NOT_PENDING");

        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-bill"));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
    }

    [Fact]
    public async Task ItIsTheSame_WhenTheTransactionIsAlreadyOfAnotherLine_Answers409_AndTheLineStaysWaiting()
    {
        await using var factory = NewFactory();
        var (ana, connection, existing, line) = await WithDuplicateAsync(factory);
        factory.Pluggy.Transactions[Checking].Add(Debit("t26-padaria-2", Noon(1).AddHours(1), 25.00m));
        await DoneAsync(factory, ana, connection);
        var second = await LineIdAsync(factory, "t26-padaria-2");
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-padaria-2"));
        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }));
        // The answer took the mark of the other line in the same save: "É a mesma" on it has nothing to be the same as.
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-padaria-2"));
        await AssertErrorAsync(
            await ConfirmAsync(ana, new { reconcile = new[] { new { id = second } } }), HttpStatusCode.Conflict, "BANK_TRANSACTION_NOT_MATCHED");

        // A line that still points to a transaction already of another line is not something the app leaves behind
        // (a synchronisation that judged it at the very moment of the answer); forced here.
        await factory.ExecuteAsync($"UPDATE bank_transactions SET matched_transaction_id = '{Sql(existing)}' WHERE id = '{Sql(second)}'");
        var refused = await ConfirmAsync(ana, new { reconcile = new[] { new { id = second } } });

        await AssertErrorAsync(
            refused, HttpStatusCode.Conflict, "MATCHED_TRANSACTION_TAKEN",
            "A transação parecida já está ligada a outro lançamento do banco. Confirme este como outra despesa ou descarte.");
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t26-padaria-2"))["review_state"]);

        // In one same request too: two lines never become the same transaction.
        await factory.ExecuteAsync($"UPDATE bank_transactions SET review_state = 'Pending', linked_transaction_id = NULL, matched_transaction_id = '{Sql(existing)}' WHERE id = '{Sql(line)}'");
        var both = await ConfirmAsync(ana, new { reconcile = new[] { new { id = line }, new { id = second } } });
        await AssertErrorAsync(both, HttpStatusCode.Conflict, "MATCHED_TRANSACTION_TAKEN");
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t26-padaria"))["review_state"]);
        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t26-padaria-2"))["review_state"]);
    }

    [Fact]
    public async Task Confirm_RefusesTheSameLineInTwoLists_ARepeatedLine_AndMoreThan500_InPortuguese_AndStoresNothing()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);

        var bodies = new (object Body, string Message)[]
        {
            (new { reconcile = new[] { new { id = line }, new { id = line } } }, "O mesmo lançamento foi enviado mais de uma vez."),
            (new { asNew = new[] { line, line } }, "O mesmo lançamento foi enviado mais de uma vez."),
            (new { reconcile = new[] { new { id = line } }, asNew = new[] { line } }, "Um lançamento só pode ter um destino por vez."),
            (new { reconcile = new[] { new { id = line } }, expenses = new[] { new { id = line } } }, "Um lançamento só pode ter um destino por vez."),
            (new { reconcile = new[] { new { id = line } }, discard = new[] { line } }, "Um lançamento só pode ter um destino por vez."),
            (new { asNew = new[] { line }, expenses = new[] { new { id = line } } }, "Um lançamento só pode ter um destino por vez."),
            (new { asNew = new[] { line }, discard = new[] { line } }, "Um lançamento só pode ter um destino por vez."),
            (new { reconcile = Enumerable.Range(0, 501).Select(_ => new { id = Guid.NewGuid() }).ToArray() }, "Envie no máximo 500 lançamentos por vez."),
            (new { asNew = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray() }, "Envie no máximo 500 lançamentos por vez."),
            (new { reconcile = Array.Empty<object>(), asNew = Array.Empty<Guid>() }, "Selecione pelo menos um lançamento."),
        };

        foreach (var (body, message) in bodies)
            await AssertErrorAsync(await ConfirmAsync(ana, body), HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", message);

        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-padaria"));
        Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"));
    }

    [Theory]
    [InlineData("""{"reconcile":[null]}""")]
    [InlineData("""{"reconcile":[{"id":null}]}""")]
    [InlineData("""{"asNew":[null]}""")]
    [InlineData("""{"asNew":["isto-nao-e-um-id"]}""")]
    [InlineData("""{"expenses":[null]}""")]
    [InlineData("""{"expenses":[{"id":null}]}""")]
    public async Task Confirm_WithAnInvalidItemInReconcileOrAsNew_Answers4xxInTheStandardFormat_NeverA500(string json)
    {
        await using var factory = NewFactory();
        var (ana, _, existing, _) = await WithDuplicateAsync(factory);

        var response = await ana.Client.PostAsync($"{Base}/review/confirm", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {raw}");
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-padaria"));
        Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"));
    }

    [Fact]
    public async Task ItIsTheSame_OfAnEntry_Answers422()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, Credit("t26-credit", Noon(), 16.00m));
        await DoneAsync(factory, ana, connection);
        var line = await LineIdAsync(factory, "t26-credit");

        foreach (var body in new object[] { new { reconcile = new[] { new { id = line } } }, new { asNew = new[] { line } } })
            await AssertErrorAsync(await ConfirmAsync(ana, body), HttpStatusCode.UnprocessableEntity, "INVALID_SELECTION", "Entradas não são confirmadas como despesa.");
    }

    [Fact]
    public async Task ItIsTheSame_WhileSomeoneDiscardsTheLineAtTheSameMoment_Answers409_AndTheTransactionIsNotTouched()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);
        var bruno = await factory.RegisterAsync("Bruno Silva", ana.JoinCode);
        factory.BeforeNextSave = async () =>
        {
            var discarded = await ConfirmAsync(bruno, new { discard = new[] { line } });
            Assert.True(HttpStatusCode.OK == discarded.StatusCode, await discarded.Content.ReadAsStringAsync());
        };

        var refused = await ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } });

        await AssertErrorAsync(refused, HttpStatusCode.Conflict, "BANK_REVIEW_CONFLICT", "A revisão mudou enquanto era confirmada. Atualize e tente de novo.");
        var row = await MirrorRowAsync(factory, "t26-padaria");
        Assert.Equal(("Discarded", null), ((string)row["review_state"]!, row["linked_transaction_id"]));
        // Nothing by halves: the transaction got nothing from the bank.
        var transaction = Assert.Single(await factory.RowsAsync($"SELECT * FROM transactions WHERE id = '{Sql(existing)}'"));
        Assert.Null(transaction["merchant"]);
        Assert.Equal("OUTROS", transaction["category"]);
    }

    [Theory]
    [InlineData(null, "OUTROS")]   // the transaction would be filled in: its update finds no row
    [InlineData("Padoca", "LAZER")] // nothing to fill in: the link of the line finds no transaction
    public async Task ItIsTheSame_WhileTheTransactionIsDeletedAtTheSameMoment_Answers409_AndTheLineIsNotReconciledWithNothing(string? merchant, string category)
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory, merchant, category);
        factory.BeforeNextSave = async () =>
        {
            factory.BeforeNextSave = null;
            Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{existing}")).StatusCode);
        };

        var refused = await ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } });

        await AssertErrorAsync(refused, HttpStatusCode.Conflict, "BANK_REVIEW_CONFLICT");
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-padaria"));
        Assert.Null((await MirrorRowAsync(factory, "t26-padaria"))["linked_transaction_id"]);
    }

    [Fact]
    public async Task DeletingTheTransactionALineWasReconciledWith_SendsTheLineBackAsDiscarded_AndItCanBeRestoredAndConfirmed()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);
        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }));

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{existing}")).StatusCode);

        var row = await MirrorRowAsync(factory, "t26-padaria");
        Assert.Equal(("Discarded", null), ((string)row["review_state"]!, row["linked_transaction_id"]));
        var review = await ReviewAsync(ana, Noon(1));
        Assert.NotNull(Find(review.GetProperty("discarded"), line));

        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { line }));
        var created = await OkAsync(ConfirmAsync(ana, new { expenses = new[] { new { id = line } } }));
        Assert.Single(created.GetProperty("created").EnumerateArray());
        Assert.Equal(3L, Assert.Single(await factory.RowsAsync("SELECT source FROM transactions"))["source"]);
    }

    // ================================================================ "É outra" (asNew)

    [Fact]
    public async Task ItIsAnotherOne_CreatesTheTransaction_ClearsTheSuspicion_AndLeavesTheExistingOneAlone()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);

        var body = await OkAsync(ConfirmAsync(ana, new { asNew = new[] { line } }));

        var created = Assert.Single(body.GetProperty("created").EnumerateArray());
        Assert.Equal(line, created.GetProperty("id").GetGuid());
        var transactionId = created.GetProperty("transactionId").GetGuid();
        Assert.NotEqual(existing, transactionId);
        Assert.Empty(body.GetProperty("reconciled").EnumerateArray());

        var row = await MirrorRowAsync(factory, "t26-padaria");
        Assert.Equal("Confirmed", row["review_state"]);
        Assert.Equal(Sql(transactionId), row["linked_transaction_id"]);
        Assert.Null(row["matched_transaction_id"]);

        var transactions = await factory.RowsAsync("SELECT * FROM transactions ORDER BY source");
        Assert.Equal(2, transactions.Count);
        // The one typed by hand got nothing from the bank.
        Assert.Equal((Sql(existing), 0L, null, "OUTROS"), ((string)transactions[0]["id"]!, (long)transactions[0]["source"]!, transactions[0]["merchant"], (string)transactions[0]["category"]!));
        // The new one is an expense come from the bank, like any other confirmed line, with the suggested category.
        Assert.Equal((Sql(transactionId), 3L, "Padaria Exemplo Ltda", "ALIMENTACAO"), ((string)transactions[1]["id"]!, (long)transactions[1]["source"]!, (string?)transactions[1]["merchant"], (string)transactions[1]["category"]!));
        Assert.Equal(Sql(ana.UserId), transactions[1]["user_id"]);
    }

    [Fact]
    public async Task TheAppAlreadyInstalled_ConfirmsASuspectedLineAsBefore_ThroughExpenses_WithTheCategoryItChose()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);

        var body = await OkAsync(ConfirmAsync(ana, new { expenses = new[] { new { id = line, category = "LAZER" } } }));

        var transactionId = Assert.Single(body.GetProperty("created").EnumerateArray()).GetProperty("transactionId").GetGuid();
        var row = await MirrorRowAsync(factory, "t26-padaria");
        Assert.Equal(("Confirmed", Sql(transactionId), null), ((string)row["review_state"]!, (string?)row["linked_transaction_id"], row["matched_transaction_id"]));
        Assert.Equal("LAZER", Assert.Single(await factory.RowsAsync($"SELECT category FROM transactions WHERE id = '{Sql(transactionId)}'"))["category"]);
        Assert.Single(await factory.RowsAsync($"SELECT id FROM transactions WHERE id = '{Sql(existing)}'"));
    }

    [Fact]
    public async Task ItIsAnotherOne_OfALineOfAnotherGroup_Answers404_AndOfALinePendingAtTheBank422()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var carla = await factory.RegisterAsync("Carla Modelo");
        await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        Bank(factory, Checking, Debit("t26-padaria", Noon(), 25.00m) with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        var line = await LineIdAsync(factory, "t26-padaria");

        await AssertErrorAsync(await ConfirmAsync(carla, new { asNew = new[] { line } }), HttpStatusCode.NotFound, "BANK_TRANSACTION_NOT_FOUND");
        await AssertErrorAsync(await ConfirmAsync(ana, new { asNew = new[] { line } }), HttpStatusCode.UnprocessableEntity, "TRANSACTION_NOT_POSTED");

        Assert.Equal("Pending", (await MirrorRowAsync(factory, "t26-padaria"))["review_state"]);
        Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"));
    }

    [Fact]
    public async Task OneRequest_CanReconcile_ConfirmAsAnother_ConfirmAndDiscard_AllOrNothing()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var padaria = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        await ManualAsync(ana, 30.00m, Noon(), "Lanche");
        Bank(factory, Checking,
            Debit("t26-same", Noon(), 25.00m),
            Debit("t26-another", Noon(), 30.00m),
            Debit("t26-plain", Noon(), 80.00m),
            Debit("t26-discard", Noon(), 5.00m));
        await DoneAsync(factory, ana, connection);
        var same = await LineIdAsync(factory, "t26-same");
        var another = await LineIdAsync(factory, "t26-another");
        var plain = await LineIdAsync(factory, "t26-plain");
        var discard = await LineIdAsync(factory, "t26-discard");

        var body = await OkAsync(ConfirmAsync(ana, new
        {
            reconcile = new[] { new { id = same } },
            asNew = new[] { another },
            expenses = new[] { new { id = plain } },
            discard = new[] { discard },
        }));

        Assert.Equal(padaria, Assert.Single(body.GetProperty("reconciled").EnumerateArray()).GetProperty("transactionId").GetGuid());
        Assert.Equal(new[] { another, plain }.Order(), body.GetProperty("created").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).Order());
        Assert.Equal(discard, Assert.Single(body.GetProperty("discarded").EnumerateArray()).GetGuid());
        Assert.Equal(4, (await factory.RowsAsync("SELECT id FROM transactions")).Count);
        Assert.Equal("Reconciled", (await MirrorRowAsync(factory, "t26-same"))["review_state"]);
        Assert.Equal("Confirmed", (await MirrorRowAsync(factory, "t26-another"))["review_state"]);
        Assert.Equal("Confirmed", (await MirrorRowAsync(factory, "t26-plain"))["review_state"]);
        Assert.Equal("Discarded", (await MirrorRowAsync(factory, "t26-discard"))["review_state"]);
    }

    // ================================================================ what a run decides, against what happens meanwhile

    [Fact]
    public async Task APurchasePendingAtTheBank_SettledUnderAnotherId_IsStillASuspectedDuplicate()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var existing = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        Bank(factory, Checking, Debit("t26-authorised", Noon(1), 25.00m, "Padaria Exemplo") with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-authorised"));

        // The bank settles the purchase under another id: the pending line is gone from its answer.
        Bank(factory, Checking, Debit("t26-settled", Noon(1), 25.00m, "Padaria Exemplo"));
        await DoneAsync(factory, ana, connection);

        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_transactions WHERE pluggy_transaction_id = 't26-authorised'"));
        // The line that left with this very reading does not hold the transaction any more.
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-settled"));
        var review = await ReviewAsync(ana, Noon(1));
        var shown = Find(review.GetProperty("expenses"), await LineIdAsync(factory, "t26-settled"))!.Value;
        Assert.Equal("Posted", shown.GetProperty("bankStatus").GetString());
        // With "match" the app leaves the line out of "Selecionar tudo": it is answered "the same" or "another one".
        Assert.Equal(existing, shown.GetProperty("match").GetProperty("transactionId").GetGuid());
        Assert.Equal(1, review.GetProperty("suspectedDuplicates").GetInt32());

        // And the answer works: nothing is created.
        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = shown.GetProperty("id").GetGuid() } } }));
        Assert.Single(await factory.RowsAsync("SELECT id FROM transactions"));
    }

    [Fact]
    public async Task APendingLineThatStaysAtTheBank_KeepsItsSuspicion_AndAnotherLineOfTheSameValueIsMarkedToo()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var existing = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        Bank(factory, Checking, Debit("t26-authorised", Noon(1), 25.00m) with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);

        // Another purchase of the same value arrives; the pending one is still listed by the bank.
        factory.Pluggy.Transactions[Checking].Add(Debit("t26-another", Noon(1).AddHours(1), 25.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-authorised"));
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-another"));
    }

    // ================================================================ a suspicion does not change transaction (review 7)

    private static async Task<JsonElement> ShownAsync(OpenFinanceApiFactory factory, Member member, string pluggyId)
    {
        var row = await MirrorRowAsync(factory, pluggyId);
        var month = ((string)row["local_date"]!)[..7];
        var review = await member.Client.GetFromJsonAsync<JsonElement>($"{Base}/review?month={month}");
        return Find(review.GetProperty("expenses"), Guid.Parse((string)row["id"]!))!.Value;
    }

    /// <summary>What "Selecionar tudo" of the app takes: the lines that can become an expense and look like nothing the app has.</summary>
    private static List<Guid> SelectAll(params JsonElement[] shown)
        => shown
            .Where(l => l.GetProperty("match").ValueKind == JsonValueKind.Null && l.GetProperty("bankStatus").GetString() == "Posted")
            .Select(l => l.GetProperty("id").GetGuid())
            .ToList();

    private static async Task<int> TransactionCountAsync(OpenFinanceApiFactory factory)
        => (await factory.RowsAsync("SELECT id FROM transactions")).Count;

    [Theory]
    [InlineData(1, 0, false)] // lunches on following days; the first settled under the same id (the probe of the review)
    [InlineData(1, 0, true)] // ... settled under another id
    [InlineData(2, 0, false)] // two days apart
    [InlineData(2, 1, false)] // two days apart, and the bank dates the settled line one day later: next to both lunches
    public async Task TwoPurchasesOfTheSameValueOnDifferentDays_BothAlreadyInTheApp_EachBankLinePointsToThePurchaseOfItsDay_AndNothingEntersTwice(
        int daysApart, int settledDaysLater, bool settledUnderAnotherId)
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var first = await ManualAsync(ana, 30.00m, Noon(), "Almoço do primeiro dia");
        var second = await ManualAsync(ana, 30.00m, Noon(daysApart), "Almoço do outro dia");
        // The first lunch is still pending at the bank.
        Bank(factory, Checking, Debit("t26-lunch-1", Noon(), 30.00m) with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(first)), await StateAsync(factory, "t26-lunch-1"));

        // The bank settles it, and the second lunch arrives in the same reading.
        var settled = settledUnderAnotherId ? "t26-lunch-1-settled" : "t26-lunch-1";
        Bank(factory, Checking, Debit(settled, Noon(settledDaysLater), 30.00m), Debit("t26-lunch-2", Noon(daysApart), 30.00m));
        await DoneAsync(factory, ana, connection);

        // Each line of the bank points to the purchase of its own day.
        Assert.Equal(("Pending", null, Sql(first)), await StateAsync(factory, settled));
        Assert.Equal(("Pending", null, Sql(second)), await StateAsync(factory, "t26-lunch-2"));
        var shownFirst = await ShownAsync(factory, ana, settled);
        var shownSecond = await ShownAsync(factory, ana, "t26-lunch-2");
        Assert.Equal(first, shownFirst.GetProperty("match").GetProperty("transactionId").GetGuid());
        Assert.Equal(second, shownSecond.GetProperty("match").GetProperty("transactionId").GetGuid());

        // "Selecionar tudo" takes neither: nothing is created...
        Assert.Empty(SelectAll(shownFirst, shownSecond));
        Assert.Equal(2, await TransactionCountAsync(factory));

        // ...and "É a mesma" on both leaves the two purchases the app had, each with its line.
        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = shownFirst.GetProperty("id").GetGuid() } } }));
        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = shownSecond.GetProperty("id").GetGuid() } } }));
        Assert.Equal(2, await TransactionCountAsync(factory));
        Assert.Equal(Sql(first), (await MirrorRowAsync(factory, settled))["linked_transaction_id"]);
        Assert.Equal(Sql(second), (await MirrorRowAsync(factory, "t26-lunch-2"))["linked_transaction_id"]);
    }

    [Fact]
    public async Task ASuspectedLinePendingAtTheBank_SettledUnderTheSameId_KeepsItsSuspicion_AndItIsTheSameIsAccepted()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var existing = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        Bank(factory, Checking, Debit("t26-authorised", Noon(1), 25.00m, "Padaria Exemplo") with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-authorised"));
        var line = await LineIdAsync(factory, "t26-authorised");

        // The bank settles the purchase: the same id, now posted.
        Bank(factory, Checking, Debit("t26-authorised", Noon(1), 25.00m, "Padaria Exemplo"));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(line, await LineIdAsync(factory, "t26-authorised"));
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-authorised"));
        var shown = await ShownAsync(factory, ana, "t26-authorised");
        Assert.Equal("Posted", shown.GetProperty("bankStatus").GetString());
        Assert.Equal(existing, shown.GetProperty("match").GetProperty("transactionId").GetGuid());

        var answer = await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }));
        Assert.Equal(existing, Assert.Single(answer.GetProperty("reconciled").EnumerateArray()).GetProperty("transactionId").GetGuid());
        Assert.Equal(1, await TransactionCountAsync(factory));
        var row = await MirrorRowAsync(factory, "t26-authorised");
        Assert.Equal(
            ("Reconciled", Sql(existing), null),
            ((string)row["review_state"]!, (string?)row["linked_transaction_id"], (string?)row["matched_transaction_id"]));
    }

    [Fact]
    public async Task TwoPurchasesOfTheSameValueOnTheSameDay_BothInTheApp_EachLineSuspectsADifferentOne_AndKeepsItWhenTheBankSettlesBoth()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var coffees = new[] { await ManualAsync(ana, 9.00m, Noon(), "Café"), await ManualAsync(ana, 9.00m, Noon().AddHours(1), "Outro café") };
        Bank(factory, Checking,
            Debit("t26-coffee-1", Noon(), 9.00m) with { Status = "PENDING" },
            Debit("t26-coffee-2", Noon().AddHours(2), 9.00m) with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        var one = (await StateAsync(factory, "t26-coffee-1")).Matched;
        var two = (await StateAsync(factory, "t26-coffee-2")).Matched;
        Assert.Equal(coffees.Select(Sql).Order(), new[] { one!, two! }.Order());

        Bank(factory, Checking, Debit("t26-coffee-1", Noon(), 9.00m), Debit("t26-coffee-2", Noon().AddHours(2), 9.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, one), await StateAsync(factory, "t26-coffee-1"));
        Assert.Equal(("Pending", null, two), await StateAsync(factory, "t26-coffee-2"));
        await OkAsync(ConfirmAsync(ana, new
        {
            reconcile = new[] { new { id = await LineIdAsync(factory, "t26-coffee-1") }, new { id = await LineIdAsync(factory, "t26-coffee-2") } },
        }));
        Assert.Equal(2, await TransactionCountAsync(factory));
    }

    [Fact]
    public async Task OneTransactionOfTheApp_AndTwoBankLinesOfTheSameValue_BothCarryTheSuspicion_AlsoAfterTheBankSettlesBoth()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var coffee = await ManualAsync(ana, 9.00m, Noon(), "Café");
        // The line of the day before and the line of the day of the coffee: either may be it, both are marked.
        Bank(factory, Checking,
            Debit("t26-coffee-day-before", Noon(-1), 9.00m) with { Status = "PENDING" },
            Debit("t26-coffee-same-day", Noon(), 9.00m) with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-day-before"));
        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-same-day"));

        Bank(factory, Checking, Debit("t26-coffee-day-before", Noon(-1), 9.00m), Debit("t26-coffee-same-day", Noon(), 9.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-day-before"));
        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-same-day"));
        Assert.Empty(SelectAll(await ShownAsync(factory, ana, "t26-coffee-day-before"), await ShownAsync(factory, ana, "t26-coffee-same-day")));
    }

    [Fact]
    public async Task WhenTheSuspectedTransactionWasDeleted_TheLinePointsToTheOtherOneThatLooksLikeIt_AndKeepsItWhenTheBankSettlesIt()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var sameDay = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        var nextDay = await ManualAsync(ana, 25.00m, Noon(1), "Padaria de novo");
        Bank(factory, Checking, Debit("t26-authorised", Noon(), 25.00m) with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(sameDay)), await StateAsync(factory, "t26-authorised"));

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{sameDay}")).StatusCode);
        // Judged again with the deletion: it does not wait unmarked for the bank to settle it.
        Assert.Equal(("Pending", null, Sql(nextDay)), await StateAsync(factory, "t26-authorised"));

        Bank(factory, Checking, Debit("t26-authorised", Noon(), 25.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(nextDay)), await StateAsync(factory, "t26-authorised"));
    }

    [Fact]
    public async Task WhenTheBankSettlesTheLineWithAnotherValue_TheSuspicionThatNoLongerHoldsIsCleared_AndTheTransactionIsFreeForTheLineThatLooksLikeIt()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var existing = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        Bank(factory, Checking, Debit("t26-authorised", Noon(), 25.00m) with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-authorised"));

        // Settled with another value (it was another purchase after all), and the purchase of R$ 25,00 arrives.
        Bank(factory, Checking, Debit("t26-authorised", Noon(), 27.50m), Debit("t26-padaria", Noon().AddHours(1), 25.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-authorised"));
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-padaria"));
    }

    // ================================================================ the ignored ones, counted for the whole group (review 7)

    [Fact]
    public async Task Review_CountsTheIgnoredOfEveryMonth_OnlyOfTheGroup_AlsoWithNothingWaiting_AndRestoringTakesTheLineOutOfTheCount()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (carla, carlaConnection) = await WithOtherBankAsync(factory, "Carla Modelo Lima", joinCode: null);
        // 35 days apart: never in the same month.
        var older = Noon(-35);
        Bank(factory, Checking,
            new FakeTransaction("t26-energy", Noon(), -180.00m) { Description = "Pagamento fatura energia", DescriptionRaw = "PAGAMENTO FATURA ENERGIA" },
            new FakeTransaction("t26-bill-older", older, -800.00m) { Description = "Pagamento de fatura", DescriptionRaw = "PAGAMENTO DE FATURA" });
        Bank(factory, OtherChecking,
            new FakeTransaction("t26-bill-carla", Noon(), -700.00m) { Description = "Pagamento de fatura", DescriptionRaw = "PAGAMENTO DE FATURA" });
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, carla, carlaConnection);
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-bill-carla"));

        // Nothing waits for the review, and the two ignored lines are counted, whatever the month asked.
        foreach (var month in new[] { Noon(), older, Noon(-70) })
        {
            var review = await ReviewAsync(ana, month);
            Assert.Equal(0, review.GetProperty("pendingAllMonths").GetInt32());
            Assert.Empty(review.GetProperty("pendingByMonth").EnumerateArray());
            Assert.Equal(2, review.GetProperty("ignoredAllMonths").GetInt32());
            Assert.Equal(
                new[] { (BrazilTime.MonthOf(Noon()), 1), (BrazilTime.MonthOf(older), 1) },
                review.GetProperty("ignoredByMonth").EnumerateArray().Select(m => (m.GetProperty("month").GetString()!, m.GetProperty("ignored").GetInt32())));
        }

        // The other group counts only its own.
        var ofCarla = await ReviewAsync(carla, Noon());
        Assert.Equal(1, ofCarla.GetProperty("ignoredAllMonths").GetInt32());
        Assert.Equal(BrazilTime.MonthOf(Noon()), Assert.Single(ofCarla.GetProperty("ignoredByMonth").EnumerateArray()).GetProperty("month").GetString());

        // Restored: it waits for the review, and is no longer counted as ignored.
        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { await LineIdAsync(factory, "t26-energy") }));
        var after = await ReviewAsync(ana, Noon());
        Assert.Equal(1, after.GetProperty("ignoredAllMonths").GetInt32());
        Assert.Equal(BrazilTime.MonthOf(older), Assert.Single(after.GetProperty("ignoredByMonth").EnumerateArray()).GetProperty("month").GetString());
        Assert.Equal(1, after.GetProperty("pendingAllMonths").GetInt32());
        Assert.Equal(1, (await ReviewAsync(carla, Noon())).GetProperty("ignoredAllMonths").GetInt32());
    }

    [Fact]
    public async Task ADiscardWhileTheRunSaves_UndoesNothingOfWhatTheRunDecidedAboutTheOtherLines_AndLeavesNoTransferByHalves()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        var existing = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        Bank(factory, Checking, Debit("t26-sent-1", Noon(), 500.00m, "PIX ENVIADO"), Debit("t26-sent-2", Noon(), 600.00m, "PIX ENVIADO"));
        // Bruno's bank has not settled his bill payment yet.
        Bank(factory, OtherChecking,
            new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA", Status = "PENDING" });
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);
        var sent2 = await LineIdAsync(factory, "t26-sent-2");
        // Now it has, and both transfers and a purchase Ana typed by hand arrive with it.
        Bank(factory, OtherChecking,
            new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" },
            Credit("t26-received-1", Noon().AddHours(1), 500.00m, "PIX RECEBIDO"),
            Credit("t26-received-2", Noon().AddHours(2), 600.00m, "PIX RECEBIDO"),
            Debit("t26-padaria", Noon(1), 25.00m, "Padaria Exemplo"));
        // The run has decided about all of them and is about to save when Ana discards one of the legs that waited.
        var happened = BeforeTheRunSaves(factory, OtherChecking, async () =>
        {
            var discarded = await ConfirmAsync(ana, new { discard = new[] { sent2 } });
            Assert.True(HttpStatusCode.OK == discarded.StatusCode, await discarded.Content.ReadAsStringAsync());
        });

        await DoneAsync(factory, bruno, brunoConnection);

        Assert.Equal(1, happened.Value);
        // The transfer nobody touched is whole, on both sides.
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-sent-1"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-received-1"));
        // The leg Ana discarded is as she left it, and its other leg is not ignored alone.
        Assert.Equal(("Discarded", null, null), await StateAsync(factory, "t26-sent-2"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-received-2"));
        // What the run decided about the lines nobody touched is kept: the one the bank has just settled and the new one.
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-bill"));
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-padaria"));
        Assert.Equal("Posted", (await MirrorRowAsync(factory, "t26-bill"))["status"]);
    }

    [Fact]
    public async Task ALegWithTwoCandidates_WhenTheFirstIsDiscardedWhileTheRunSaves_PairsWithTheOther()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, Checking, Debit("t26-out-1", Noon(), 50.00m), Debit("t26-out-2", Noon().AddHours(1), 50.00m));
        Bank(factory, OtherChecking, Credit("t26-in", Noon().AddHours(2), 50.00m));
        await DoneAsync(factory, ana, anaConnection);
        var first = await LineIdAsync(factory, "t26-out-1");
        var happened = BeforeTheRunSaves(factory, OtherChecking, async () =>
        {
            var discarded = await ConfirmAsync(ana, new { discard = new[] { first } });
            Assert.True(HttpStatusCode.OK == discarded.StatusCode, await discarded.Content.ReadAsStringAsync());
        });

        await DoneAsync(factory, bruno, brunoConnection);

        Assert.Equal(1, happened.Value);
        Assert.Equal(("Discarded", null, null), await StateAsync(factory, "t26-out-1"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-in"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-out-2"));
    }

    [Fact]
    public async Task ABillPaymentRestoredWhileTheRunPairsIt_StaysAsThePersonLeftIt_AndTheCreditOnTheCardIsNotIgnoredAlone()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, Debit("t26-bill-out", Noon(), 1200.00m, "PAGAMENTO DE FATURA"));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-bill-out"));
        var billOut = await LineIdAsync(factory, "t26-bill-out");
        Bank(factory, Card, Credit("t26-bill-in", Noon(), 1200.00m, "Pagamento recebido"));
        var happened = BeforeTheRunSaves(factory, Card, async () =>
        {
            var restored = await ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { billOut });
            Assert.True(HttpStatusCode.OK == restored.StatusCode, await restored.Content.ReadAsStringAsync());
        });

        await DoneAsync(factory, ana, connection);

        Assert.Equal(1, happened.Value);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-bill-out"));
        Assert.NotNull((await MirrorRowAsync(factory, "t26-bill-out"))["person_decided_at_utc"]);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-bill-in"));
    }

    [Fact]
    public async Task WhenWhoHeldTheOtherLegLeavesTheGroupWhileTheRunPairsIt_TheLegThatArrivedIsNotIgnoredAlone()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, OtherChecking, Credit("t26-received", Noon(), 500.00m));
        await DoneAsync(factory, bruno, brunoConnection);
        Bank(factory, Checking,
            Debit("t26-sent", Noon(), 500.00m),
            new FakeTransaction("t26-bill", Noon(), -900.00m) { DescriptionRaw = "PAGAMENTO DE FATURA" });
        var happened = BeforeTheRunSaves(factory, Checking, async () =>
        {
            var left = await bruno.Client.PostAsJsonAsync("/api/v1/couples/leave", new { });
            Assert.True(left.IsSuccessStatusCode, await left.Content.ReadAsStringAsync());
        });

        await DoneAsync(factory, ana, anaConnection);

        Assert.Equal(1, happened.Value);
        Assert.Empty(await factory.RowsAsync("SELECT id FROM bank_transactions WHERE pluggy_transaction_id = 't26-received'"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-sent"));
        Assert.Equal(("Ignored", "BillPayment", null), await StateAsync(factory, "t26-bill"));
    }

    [Fact]
    public async Task WhenTheTransactionALineWasAboutToSuspectIsDeletedWhileTheRunSaves_NothingIsWrittenByHalves_AndTheNextRunLeavesTheLineWaiting()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var existing = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        Bank(factory, Checking, Debit("t26-padaria", Noon(1), 25.00m), Debit("t26-market", Noon(1), 80.00m));
        var happened = BeforeTheRunSaves(factory, Checking, async () =>
            Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{existing}")).StatusCode));

        var failed = await SyncAsync(factory, ana, connection);

        // The line cannot point to a transaction that is gone: the save of the account is refused whole and the run
        // says so. Nothing of that account was written.
        Assert.Equal(1, happened.Value);
        Assert.Equal("Failed", failed["status"]);
        Assert.Empty(await MirrorAsync(factory));
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));

        // The next one reads the same lines as new: there is nothing left to suspect.
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-padaria"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-market"));
    }

    [Fact]
    public async Task ItIsTheSame_AnsweredWhileTheTransactionIsBeingDeleted_LeavesTheLineAsDiscarded_VisibleAndRestorable()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);
        // The deletion has read which lines point to the transaction (none yet) when the answer is stored; only
        // then is the transaction deleted, and the link of the line goes with it.
        factory.BeforeNextSave = async () =>
        {
            var answered = await ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } });
            Assert.True(HttpStatusCode.OK == answered.StatusCode, await answered.Content.ReadAsStringAsync());
        };

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{existing}")).StatusCode);

        Assert.Null(factory.BeforeNextSave); // the answer did happen in the middle
        Assert.Empty(await factory.RowsAsync("SELECT id FROM transactions"));
        Assert.Null((await MirrorRowAsync(factory, "t26-padaria"))["linked_transaction_id"]);
        // Without its transaction the line is a discarded one: it is listed as such, never lost.
        var review = await ReviewAsync(ana, Noon(1));
        Assert.NotNull(Find(review.GetProperty("discarded"), line));
        Assert.Null(Find(review.GetProperty("expenses"), line));
        Assert.Null(Find(review.GetProperty("ignored"), line));
        // Answering again finds nothing to answer; discarding it again is done already; it is not one to confirm yet.
        Assert.Empty((await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }))).GetProperty("reconciled").EnumerateArray());
        Assert.Equal(line, Assert.Single((await OkAsync(ConfirmAsync(ana, new { discard = new[] { line } }))).GetProperty("discarded").EnumerateArray()).GetGuid());
        await AssertErrorAsync(await ConfirmAsync(ana, new { expenses = new[] { new { id = line } } }), HttpStatusCode.Conflict, "BANK_TRANSACTION_NOT_PENDING");

        // And it comes back like any discarded line: restored, then confirmed as an expense.
        var restored = await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { line }));
        Assert.Equal(line, Assert.Single(restored.GetProperty("restored").EnumerateArray()).GetGuid());
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-padaria"));
        Assert.NotNull(Find((await ReviewAsync(ana, Noon(1))).GetProperty("expenses"), line));
        var created = await OkAsync(ConfirmAsync(ana, new { expenses = new[] { new { id = line } } }));
        Assert.Single(created.GetProperty("created").EnumerateArray());
        Assert.Equal("Confirmed", (await MirrorRowAsync(factory, "t26-padaria"))["review_state"]);
    }

    [Fact]
    public async Task ALineReconciledWithATransactionThatIsStillThere_IsNotListedAsDiscarded_NorRestored_NorDiscarded()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);
        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }));

        var review = await ReviewAsync(ana, Noon(1));
        Assert.Empty(review.GetProperty("discarded").EnumerateArray());
        var restored = await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { line }));
        Assert.Empty(restored.GetProperty("restored").EnumerateArray());
        await AssertErrorAsync(await ConfirmAsync(ana, new { discard = new[] { line } }), HttpStatusCode.Conflict, "BANK_TRANSACTION_NOT_PENDING");

        var row = await MirrorRowAsync(factory, "t26-padaria");
        Assert.Equal(("Reconciled", Sql(existing)), ((string)row["review_state"]!, (string?)row["linked_transaction_id"]));
    }

    // ================================================================ a purchase on a credit card is never a leg of a transfer

    [Fact]
    public async Task APurchaseOnTheCardOfOne_AndMoneyReceivedByTheOtherOnTheSameDay_AreNotATransfer_InAnyOrderOfArrival()
    {
        await using var factory = NewFactory();
        var (ana, anaConnection) = await AnaAsync(factory);
        var (bruno, brunoConnection) = await WithOtherBankAsync(factory, "Bruno Silva", ana.JoinCode);
        Bank(factory, Card, Debit("t26-card-purchase", Noon(), 100.00m, "Loja Exemplo"));
        Bank(factory, OtherChecking, Credit("t26-pix-in", Noon().AddHours(1), 100.00m, "PIX RECEBIDO"));

        // The purchase waits; the credit arrives.
        await DoneAsync(factory, ana, anaConnection);
        await DoneAsync(factory, bruno, brunoConnection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-card-purchase"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-pix-in"));

        // The other order: the credit waits; the purchase arrives.
        factory.Pluggy.Transactions[OtherChecking].Add(Credit("t26-pix-in-2", Noon(1), 110.00m, "PIX RECEBIDO"));
        await DoneAsync(factory, bruno, brunoConnection);
        factory.Pluggy.Transactions[Card].Add(Debit("t26-card-purchase-2", Noon(1).AddHours(1), 110.00m, "Loja Exemplo"));
        await DoneAsync(factory, ana, anaConnection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-card-purchase-2"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-pix-in-2"));

        // Money that ENTERS the card is still the other leg of what left a checking account (the payment of the bill).
        factory.Pluggy.Transactions[Checking].Add(Debit("t26-paid", Noon(2), 120.00m, "PIX ENVIADO"));
        factory.Pluggy.Transactions[Card].Add(Credit("t26-paid-in", Noon(2), 120.00m, "Pagamento recebido"));
        await DoneAsync(factory, ana, anaConnection);
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-paid"));
        Assert.Equal(("Ignored", "Transfer", null), await StateAsync(factory, "t26-paid-in"));
    }

    // ================================================================ every line that may be a duplicate is marked (review 9)

    private static async Task<Guid?> MatchShownAsync(OpenFinanceApiFactory factory, Member member, string pluggyId)
    {
        var match = (await ShownAsync(factory, member, pluggyId)).GetProperty("match");
        return match.ValueKind == JsonValueKind.Null ? null : match.GetProperty("transactionId").GetGuid();
    }

    /// <summary>What "Selecionar tudo" would send now, of the lines given that still wait.</summary>
    private static async Task<List<Guid>> SelectAllOfAsync(OpenFinanceApiFactory factory, Member member, params string[] pluggyIds)
    {
        var shown = new List<JsonElement>();
        foreach (var id in pluggyIds)
            if ((string)(await MirrorRowAsync(factory, id))["review_state"]! == "Pending") shown.Add(await ShownAsync(factory, member, id));
        return SelectAll(shown.ToArray());
    }

    [Theory]
    [InlineData(true)] // the duplicate is in the checking account, the other purchase on the card (the probe of the review)
    [InlineData(false)] // the other way round: the order the accounts are read in decides nothing
    public async Task ProbeX2_AccountAndCardInTheSameRun_BothLinesAreMarked_ItIsAnotherOneOnOneLeavesTheOtherMarked_AndNothingEntersTwice(
        bool duplicateInChecking)
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var fuel = await ManualAsync(ana, 50.00m, Noon(), "Posto Exemplo");
        var (duplicateAccount, otherAccount) = duplicateInChecking ? (Checking, Card) : (Card, Checking);
        Bank(factory, duplicateAccount, Debit("px2-same-day", Noon().AddHours(1), 50.00m, "Posto Exemplo"));
        Bank(factory, otherAccount, Debit("px2-next-day", Noon(1), 50.00m, "Outra compra"));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(fuel)), await StateAsync(factory, "px2-same-day"));
        Assert.Equal(("Pending", null, Sql(fuel)), await StateAsync(factory, "px2-next-day"));
        Assert.Equal(fuel, await MatchShownAsync(factory, ana, "px2-same-day"));
        Assert.Equal(fuel, await MatchShownAsync(factory, ana, "px2-next-day"));
        Assert.Empty(await SelectAllOfAsync(factory, ana, "px2-same-day", "px2-next-day"));

        // "É outra" on the line that is another purchase: only that line loses the mark.
        await OkAsync(ConfirmAsync(ana, new { asNew = new[] { await LineIdAsync(factory, "px2-next-day") } }));

        Assert.Equal(("Pending", null, Sql(fuel)), await StateAsync(factory, "px2-same-day"));
        Assert.Equal(fuel, await MatchShownAsync(factory, ana, "px2-same-day"));
        Assert.Empty(await SelectAllOfAsync(factory, ana, "px2-same-day", "px2-next-day"));
        Assert.Equal(2, await TransactionCountAsync(factory));

        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = await LineIdAsync(factory, "px2-same-day") } } }));
        Assert.Equal(2, await TransactionCountAsync(factory));
        Assert.Equal(Sql(fuel), (await MirrorRowAsync(factory, "px2-same-day"))["linked_transaction_id"]);
    }

    [Theory]
    [InlineData("py-padaria-a", "py-cafe-b")]
    [InlineData("py-cafe-b", "py-padaria-a")]
    public async Task ProbeY_OneAccount_TwoLinesOfTheSameValueOnTheSameDay_OneTransaction_BothAreMarked_AndTheTwoAnswersLeaveTwoTransactions(
        string answeredAnother, string answeredTheSame)
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var coffee = await ManualAsync(ana, 9.00m, Noon(), "Cafe B");
        Bank(factory, Checking, Debit("py-padaria-a", Noon().AddHours(-1), 9.00m, "Padaria A"), Debit("py-cafe-b", Noon().AddHours(1), 9.00m, "Cafe B"));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "py-padaria-a"));
        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "py-cafe-b"));
        Assert.Empty(await SelectAllOfAsync(factory, ana, "py-padaria-a", "py-cafe-b"));

        await OkAsync(ConfirmAsync(ana, new { asNew = new[] { await LineIdAsync(factory, answeredAnother) } }));
        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, answeredTheSame));
        Assert.Empty(await SelectAllOfAsync(factory, ana, "py-padaria-a", "py-cafe-b"));

        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = await LineIdAsync(factory, answeredTheSame) } } }));
        Assert.Equal(2, await TransactionCountAsync(factory));
    }

    [Fact]
    public async Task ProbeY_DiscardingOneMarkedLine_LeavesTheOtherMarked()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var coffee = await ManualAsync(ana, 9.00m, Noon(), "Cafe B");
        Bank(factory, Checking, Debit("py-padaria-a", Noon().AddHours(-1), 9.00m, "Padaria A"), Debit("py-cafe-b", Noon().AddHours(1), 9.00m, "Cafe B"));
        await DoneAsync(factory, ana, connection);

        await OkAsync(ConfirmAsync(ana, new { discard = new[] { await LineIdAsync(factory, "py-padaria-a") } }));

        Assert.Equal(("Discarded", null, null), await StateAsync(factory, "py-padaria-a"));
        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "py-cafe-b"));
        Assert.Empty(await SelectAllOfAsync(factory, ana, "py-cafe-b"));
    }

    [Fact]
    public async Task ProbeZ_TheLineOfTheDayNextToItArrivesInOneRun_AndTheLineOfTheSameDayInTheNext_BothAreMarked()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var lunch = await ManualAsync(ana, 30.00m, Noon(), "Almoço");
        Bank(factory, Checking, Debit("pz-next-day", Noon(1), 30.00m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(lunch)), await StateAsync(factory, "pz-next-day"));

        factory.Pluggy.Transactions[Checking].Add(Debit("pz-same-day", Noon().AddHours(1), 30.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(lunch)), await StateAsync(factory, "pz-same-day"));
        Assert.Equal(("Pending", null, Sql(lunch)), await StateAsync(factory, "pz-next-day"));
        Assert.Empty(await SelectAllOfAsync(factory, ana, "pz-same-day", "pz-next-day"));
    }

    [Fact]
    public async Task TwoEqualPurchasesInTheApp_AndTwoEqualLines_EachItIsTheSameTakesOne_AndTheOtherLineThenPointsToTheOtherPurchase()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var first = await ManualAsync(ana, 9.00m, Noon(), "Café");
        Bank(factory, Checking, Debit("t26-coffee-1", Noon(), 9.00m), Debit("t26-coffee-2", Noon().AddHours(2), 9.00m));
        await DoneAsync(factory, ana, connection);
        // Both lines look like the only purchase the app had when they arrived...
        Assert.Equal(("Pending", null, Sql(first)), await StateAsync(factory, "t26-coffee-1"));
        Assert.Equal(("Pending", null, Sql(first)), await StateAsync(factory, "t26-coffee-2"));
        // ...and the second purchase is typed afterwards.
        var second = await ManualAsync(ana, 9.00m, Noon().AddHours(2), "Outro café");

        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = await LineIdAsync(factory, "t26-coffee-1") } } }));

        // In the same save, the other line was judged again: the purchase taken is no longer one to point to.
        Assert.Equal(("Pending", null, Sql(second)), await StateAsync(factory, "t26-coffee-2"));
        Assert.Equal(second, await MatchShownAsync(factory, ana, "t26-coffee-2"));
        Assert.Empty(await SelectAllOfAsync(factory, ana, "t26-coffee-2"));

        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = await LineIdAsync(factory, "t26-coffee-2") } } }));
        Assert.Equal(2, await TransactionCountAsync(factory));
        Assert.Equal(Sql(first), (await MirrorRowAsync(factory, "t26-coffee-1"))["linked_transaction_id"]);
        Assert.Equal(Sql(second), (await MirrorRowAsync(factory, "t26-coffee-2"))["linked_transaction_id"]);
    }

    [Fact]
    public async Task OneTransaction_AndThreeLines_ItIsTheSameOnTheFirst_TakesTheMarkOfTheOthers_WhichCanThenBeConfirmedAsNewExpenses()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var coffee = await ManualAsync(ana, 9.00m, Noon(), "Café");
        Bank(factory, Checking, Debit("t26-coffee-1", Noon(), 9.00m), Debit("t26-coffee-2", Noon().AddHours(2), 9.00m));
        Bank(factory, Card, Debit("t26-coffee-card", Noon(1), 9.00m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(3, (await factory.RowsAsync($"SELECT id FROM bank_transactions WHERE matched_transaction_id = '{Sql(coffee)}'")).Count);

        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = await LineIdAsync(factory, "t26-coffee-1") } } }));

        // No other line of the group still points to the purchase that now has its line (also the one of another account).
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-coffee-2"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-coffee-card"));
        Assert.Null(await MatchShownAsync(factory, ana, "t26-coffee-2"));
        var selected = await SelectAllOfAsync(factory, ana, "t26-coffee-2", "t26-coffee-card");
        Assert.Equal(2, selected.Count);

        await OkAsync(ConfirmAsync(ana, new { expenses = selected.Select(id => new { id }).ToArray() }));
        Assert.Equal(3, await TransactionCountAsync(factory));
        Assert.Equal("Confirmed", (await MirrorRowAsync(factory, "t26-coffee-2"))["review_state"]);
    }

    [Fact]
    public async Task ATransactionCreatedFromTheBank_IsNeverWhatAnotherLineLooksLike()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        Bank(factory, Checking, Debit("t26-first", Noon(), 44.00m));
        await DoneAsync(factory, ana, connection);
        await OkAsync(ConfirmAsync(ana, new { expenses = new[] { new { id = await LineIdAsync(factory, "t26-first") } } }));

        // The same value on the same day, on the card: the expense confirmed from the bank is not a candidate.
        Bank(factory, Card, Debit("t26-second", Noon().AddHours(1), 44.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-second"));
    }

    [Fact]
    public async Task DeletingTheTransactionALinePointsTo_JudgesTheLineAgain_ItPointsToTheOtherPurchaseThatLooksLikeIt()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var sameDay = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        var nextDay = await ManualAsync(ana, 25.00m, Noon(1), "Padaria de novo");
        // Already settled at the bank: no later reading would judge this line again.
        Bank(factory, Checking, Debit("t26-padaria", Noon(), 25.00m));
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(sameDay)), await StateAsync(factory, "t26-padaria"));

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{sameDay}")).StatusCode);

        Assert.Equal(("Pending", null, Sql(nextDay)), await StateAsync(factory, "t26-padaria"));
        Assert.Empty(await SelectAllOfAsync(factory, ana, "t26-padaria"));

        Assert.Equal(HttpStatusCode.NoContent, (await ana.Client.DeleteAsync($"/api/v1/transactions/{nextDay}")).StatusCode);
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-padaria"));
    }

    [Fact]
    public async Task RestoringADiscardedLine_ThatLooksLikeATransactionOfTheApp_BringsItBackMarked_NotIntoSelectAll()
    {
        await using var factory = NewFactory();
        var (ana, _, existing, line) = await WithDuplicateAsync(factory);
        await OkAsync(ConfirmAsync(ana, new { discard = new[] { line } }));
        Assert.Equal(("Discarded", null, null), await StateAsync(factory, "t26-padaria"));

        await OkAsync(ana.Client.PostAsJsonAsync($"{Base}/review/restore", new[] { line }));

        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-padaria"));
        Assert.Empty(await SelectAllOfAsync(factory, ana, "t26-padaria"));
        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }));
        Assert.Equal(1, await TransactionCountAsync(factory));
    }

    [Fact]
    public async Task ASuspectedLineTheBankSettlesInAnotherCurrency_LosesTheMark()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var existing = await ManualAsync(ana, 25.00m, Noon(), "Padaria");
        Bank(factory, Checking, Debit("t26-authorised", Noon(), 25.00m) with { Status = "PENDING" });
        await DoneAsync(factory, ana, connection);
        Assert.Equal(("Pending", null, Sql(existing)), await StateAsync(factory, "t26-authorised"));

        Bank(factory, Checking, Debit("t26-authorised", Noon(), 25.00m) with { CurrencyCode = "USD" });
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-authorised"));
    }
}
