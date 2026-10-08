using System.Net;
using System.Net.Http.Json;
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

    /// <summary>Noon in Brasília, some days ago (inside the first window of a connection, in a month that is over or not).</summary>
    private static DateTime Noon(int daysAfterTheBase = 0) => DateTime.UtcNow.Date.AddDays(-10 + daysAfterTheBase).AddHours(15);

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
        var lateEvening = DateTime.UtcNow.Date.AddDays(-10).AddDays(1).AddHours(1);
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
        await factory.ExecuteAsync($"UPDATE transactions SET source = 1, bank = 'OCR' WHERE id = '{Sql(imported)}'");
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

    [Fact]
    public async Task OneTransactionOfTheApp_IsSuspectedByOneBankLineOnly_AlsoInALaterSynchronisation()
    {
        await using var factory = NewFactory();
        var (ana, connection) = await AnaAsync(factory);
        var coffee = await ManualAsync(ana, 9.00m, Noon(), "Café");
        // Two coffees of the same value on the same day at the bank, one of them typed by hand.
        Bank(factory, Checking, Debit("t26-coffee-1", Noon(), 9.00m), Debit("t26-coffee-2", Noon().AddHours(2), 9.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-1"));
        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-coffee-2"));

        // A third one arrives later: the transaction is already under suspicion of the first line.
        factory.Pluggy.Transactions[Checking].Add(Debit("t26-coffee-3", Noon().AddHours(3), 9.00m));
        await DoneAsync(factory, ana, connection);

        Assert.Equal(("Pending", null, null), await StateAsync(factory, "t26-coffee-3"));
        Assert.Equal(("Pending", null, Sql(coffee)), await StateAsync(factory, "t26-coffee-1"));
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
        Assert.Single(await factory.RowsAsync("SELECT id FROM bank_transactions WHERE pluggy_transaction_id = 't26-received'"));
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
        Bank(factory, OtherChecking, Credit("t26-received", Noon(), 500.00m, "PIX RECEBIDO"));
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
        // Never the raw JSON, nor the name of who paid.
        Assert.DoesNotContain(FakePluggyServer.RawOnlyMarker, review.GetRawText(), StringComparison.Ordinal);

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
        // Two lines suspecting the same transaction is not something a synchronisation leaves behind; forced here.
        await factory.ExecuteAsync($"UPDATE bank_transactions SET matched_transaction_id = '{Sql(existing)}' WHERE id = '{Sql(second)}'");
        await OkAsync(ConfirmAsync(ana, new { reconcile = new[] { new { id = line } } }));

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
}
