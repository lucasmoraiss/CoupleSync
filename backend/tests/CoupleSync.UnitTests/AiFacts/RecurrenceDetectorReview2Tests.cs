using CoupleSync.Application.AiFacts;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.AiFacts;

/// <summary>
/// Issue #39, review round 2 — the same charge seen twice (by the two phones of the couple, or a day apart) is one
/// stream; two streams of the same amount only when they are clearly apart; a long number in a description only
/// drops the charge when it is a document or a contact; and what is older than 13 months only counts for a yearly
/// stream. "Today" is always given; establishments and numbers are made up.
/// </summary>
public sealed class RecurrenceDetectorReview2Tests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static readonly Guid Ana = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bruno = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly RecurrenceOptions Options = new();

    private static RecurrenceRow Row(string? merchant, decimal amount, DateOnly date, string category = "LAZER", string? description = null, Guid? user = null, int hour = 15, TransactionSource source = TransactionSource.Manual)
        // 15:00 UTC is noon in Brasília: the local date is the date given.
        => new(Guid.NewGuid(), date.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc), amount, merchant, description, category, user ?? Ana, source);

    private static List<RecurrenceRow> Monthly(string? merchant, decimal amount, DateOnly first, int count, string category = "LAZER", string? description = null, Guid? user = null, int hour = 15, TransactionSource source = TransactionSource.Manual)
        => Enumerable.Range(0, count).Select(i => Row(merchant, amount, first.AddMonths(i), category, description, user, hour, source)).ToList();

    private static List<RecurrenceRow> OnDays(string merchant, decimal amount, int firstMonth, int[] days, string category = "LAZER", Guid? user = null)
        => days.Select((day, i) => Row(merchant, amount, new DateOnly(2026, firstMonth + i, day), category, user: user)).ToList();

    private static IReadOnlyList<DetectedStream> Detect(IEnumerable<RecurrenceRow> rows, DateOnly? today = null)
        => RecurrenceDetector.Detect(rows.ToList(), today ?? Today, Options).Streams;

    // ---------------------------------------------------------------- N1: the same charge, seen by the two phones

    /// <summary>
    /// A shared card: the phone of each person captures the notification of the same charge. One stream, one
    /// charge a month — in the narrow band (LAZER) and in the wide one (MORADIA).
    /// </summary>
    [Theory]
    [InlineData(3, "LAZER", 0)]
    [InlineData(6, "LAZER", 0)]
    [InlineData(4, "MORADIA", 0)]
    // The neighbour: one of the two is registered a day (or a few days) later — capture date against purchase date.
    [InlineData(3, "LAZER", 1)]
    [InlineData(6, "LAZER", 1)]
    [InlineData(4, "MORADIA", 1)]
    [InlineData(5, "LAZER", 2)]
    [InlineData(5, "LAZER", 4)]
    public void TheSameChargeRegisteredByBothPeople_IsOneStream_AndItsCostIsNotDoubled(int months, string category, int daysApart)
    {
        var first = new DateOnly(2026, 10, 5).AddMonths(-(months - 1));
        var rows = Monthly("Streaming Exemplo", 39.90m, first, months, category, user: Ana)
            .Concat(Monthly("Streaming Exemplo", 39.90m, first.AddDays(daysApart), months, category, user: Bruno, hour: 16))
            .ToList();

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringCadences.Monthly, stream.Cadence);
        Assert.Equal(months, stream.Facts.Occurrences);
        Assert.Equal(39.90m, stream.Facts.MedianAmount);
        Assert.Equal(478.80m, stream.Facts.AnnualCost);
        Assert.Equal(months, stream.TransactionIds.Count);
        // Registered by both: it is of nobody in particular.
        Assert.Null(stream.Facts.UserId);
    }

    /// <summary>One of the phones only caught some of the charges, a day later.</summary>
    [Fact]
    public void TheSameCharge_PartlyRegisteredByTheOtherPerson_IsOneStream()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 6, 5), 5, user: Ana)
            .Concat(Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 6, 6), 4, user: Bruno));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(5, stream.Facts.Occurrences);
    }

    /// <summary>The same person has the charge twice: the statement they imported and the notification, a day apart.</summary>
    [Fact]
    public void TheSameCharge_RegisteredTwiceByTheSamePerson_ADayApart_IsOneStream()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 6, 5), 5, source: TransactionSource.OcrImport)
            .Concat(Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 6, 6), 5, source: TransactionSource.Notification));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(5, stream.Facts.Occurrences);
        Assert.Equal(478.80m, stream.Facts.AnnualCost);
    }

    /// <summary>A year of the same charge seen twice does not leave a yearly stream made of the copies.</summary>
    [Fact]
    public void AYearOfTheSameChargeSeenTwice_IsOnlyTheMonthlyStream()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2025, 10, 5), 13, user: Ana)
            .Concat(Monthly("Streaming Exemplo", 39.90m, new DateOnly(2025, 10, 6), 13, user: Bruno));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringCadences.Monthly, stream.Cadence);
        Assert.Equal(13, stream.Facts.Occurrences);
    }

    /// <summary>The other side: the plan of each person, charged far from each other, is still two streams.</summary>
    [Fact]
    public void ThePlanOfEachPerson_ChargedFarApart_IsStillTwoStreams()
    {
        var rows = Monthly("Musica Exemplo", 21.90m, new DateOnly(2026, 6, 3), 5, user: Ana)
            .Concat(Monthly("Musica Exemplo", 21.90m, new DateOnly(2026, 6, 10), 5, user: Bruno));

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.Equal(new Guid?[] { Ana, Bruno }, streams.Select(s => s.Facts.UserId).Order());
    }

    // ---------------------------------------------------------------- M4: two streams only when clearly apart

    /// <summary>
    /// A purchase of the same amount twice a month around the same days (fuel around the 5th and the 20th) is not
    /// two fixed bills: the days of a person who buys move, the day of a bill does not.
    /// </summary>
    [Fact]
    public void APurchaseTwiceAMonthOnSimilarDays_IsNotTwoFixedBills()
    {
        var rows = OnDays("Posto Exemplo", 200m, 6, [5, 8, 4, 7, 5], "TRANSPORTE")
            .Concat(OnDays("Posto Exemplo", 200m, 6, [20, 17, 22, 19, 21], "TRANSPORTE"));

        var streams = Detect(rows);

        Assert.True(streams.Count <= 1, $"{streams.Count} streams");
        Assert.True(streams.Sum(s => s.Facts.AnnualCost) <= 2400m);
    }

    /// <summary>
    /// Each on its own day of the month, three days from each other, of the same person and registered the same
    /// way: the same phone does not capture one charge twice on different days — two subscriptions of the same
    /// price (review round 3: this test used to lock one stream, which hid a real charge).
    /// </summary>
    [Fact]
    public void TwoSeriesOfTheSamePerson_RegisteredTheSameWay_OnCloseDays_AreTwoStreams()
    {
        var rows = Monthly("APPLE.COM/BILL", 21.90m, new DateOnly(2026, 6, 3), 5).Concat(Monthly("APPLE.COM/BILL", 21.90m, new DateOnly(2026, 6, 6), 5));

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.Equal(5, s.Facts.Occurrences));
        Assert.Equal(525.60m, streams.Sum(s => s.Facts.AnnualCost));
    }

    /// <summary>The same two series, one from the statement and one from the notification: one charge seen twice, one stream.</summary>
    [Fact]
    public void TwoSeriesOfTheSamePerson_RegisteredInDifferentWays_OnCloseDays_AreOneStream()
    {
        var rows = Monthly("APPLE.COM/BILL", 21.90m, new DateOnly(2026, 6, 3), 5, source: TransactionSource.OcrImport)
            .Concat(Monthly("APPLE.COM/BILL", 21.90m, new DateOnly(2026, 6, 6), 5, source: TransactionSource.Notification));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(5, stream.Facts.Occurrences);
    }

    /// <summary>The other side: each always on its day (a day of difference at most), the days far apart — two streams.</summary>
    [Fact]
    public void TwoSeriesEachOnItsOwnDay_FarApart_AreTwoStreams_AlsoWhenTheDayMovesByOne()
    {
        var rows = OnDays("APPLE.COM/BILL", 21.90m, 6, [3, 3, 4, 3, 3]).Concat(OnDays("APPLE.COM/BILL", 21.90m, 6, [17, 18, 17, 17, 17]));

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.Equal(5, s.Facts.Occurrences));
    }

    // ---------------------------------------------------------------- M1: a long number in a description

    /// <summary>
    /// A statement line with a client code or a contract number of 9 to 14 digits: the number is not a valid CPF
    /// nor a valid CNPJ, has no punctuation of a document or of a phone and no word that says what it is. It only
    /// leaves the name.
    /// </summary>
    [Theory]
    [InlineData("DEB AUT ENERGIA EXEMPLO 0012345678")]         // 10 digits
    [InlineData("DEB AUT ENERGIA EXEMPLO 912345678")]          // 9 digits, starts with 9
    [InlineData("DEB AUT ENERGIA EXEMPLO 12345678900")]        // 11 digits, not a valid CPF
    [InlineData("DEB AUT ENERGIA EXEMPLO 001234567890")]       // 12 digits
    [InlineData("DEB AUT ENERGIA EXEMPLO 12345678000100")]     // 14 digits, not a valid CNPJ
    public void ARawLongNumberThatIsNoDocument_OnlyLeavesTheName_AndTheStreamIsFormed(string description)
    {
        var stream = Assert.Single(Detect(Monthly(null, 150m, new DateOnly(2026, 6, 5), 5, "MORADIA", description)));

        Assert.Equal("deb aut energia exemplo", stream.MerchantKey);
        Assert.Equal("DEB AUT ENERGIA EXEMPLO", stream.Facts.DisplayName);
        Assert.Equal(RecurringNameSources.Description, stream.Facts.NameSource);
    }

    /// <summary>What keeps dropping the charge: a valid document, the punctuation of a document or of a phone, or a word that says what the number is.</summary>
    [Theory]
    [InlineData("Mensalidade Escola Exemplo 12345678909")]           // valid CPF, raw
    [InlineData("Boleto Exemplo Ltda 12345678000195")]               // valid CNPJ, raw
    [InlineData("Mensalidade Escola Exemplo 123.456.789-00")]        // the punctuation of a CPF, whatever the digits
    [InlineData("Boleto Exemplo Ltda 12.345.678/0001-00")]           // the punctuation of a CNPJ
    [InlineData("Aula Exemplo 11 91234-5678")]                       // the punctuation of a phone
    [InlineData("Aula Exemplo 91234-5678")]
    [InlineData("Mensalidade Escola Exemplo CPF 12345678900")]       // a word that says it is a document
    [InlineData("Boleto Exemplo CNPJ: 12345678000100")]
    [InlineData("Aula Exemplo tel 1198765432")]                      // a word that says it is a phone
    [InlineData("Aula Exemplo WhatsApp 11987654321")]
    [InlineData("Aula Exemplo contato 0012345678")]
    public void ADocumentOrAContactInADescription_StillFormsNoStream(string description)
    {
        var result = RecurrenceDetector.Detect(Monthly(null, 150m, new DateOnly(2026, 6, 5), 5, "OUTROS", description), Today, Options);

        Assert.Empty(result.Streams);
        Assert.Empty(result.ChargesByKey);
    }

    /// <summary>The words that say a number is a document or a contact: changing the list is a decision, not an accident.</summary>
    [Fact]
    public void TheWordsThatSayANumberIsADocumentOrAContact_AreTheseOnes()
        => Assert.Equal(
            ["cpf", "cnpj", "rg", "doc", "documento", "tel", "telefone", "fone", "cel", "celular", "whatsapp", "whats", "zap", "contato", "fax", "pix", "chave"],
            CoupleSync.Application.Ai.FactPackPrivacyFilter.DocumentOrContactWords);

    // ---------------------------------------------------------------- yearly: what is older than 13 months

    /// <summary>The yearly charge of 14 months ago and its renewal of 2 months ago are found today.</summary>
    [Fact]
    public void AYearlyCharge_WhoseFirstChargeIsOlderThan13Months_IsFound()
    {
        var rows = new[]
        {
            Row("Anuidade Exemplo", 120m, Today.AddMonths(-14)),
            Row("Anuidade Exemplo", 120m, Today.AddMonths(-2)),
        };

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringCadences.Yearly, stream.Cadence);
        Assert.Equal(RecurringStatuses.Active, stream.Facts.Status);
        Assert.Equal(2, stream.Facts.Occurrences);
        Assert.Equal(120m, stream.Facts.AnnualCost);
        Assert.Equal(Today.AddMonths(-2).AddYears(1), stream.Facts.NextExpectedLocal);
    }

    /// <summary>Three renewals in 26 months: the three are charges of the stream.</summary>
    [Fact]
    public void ThreeRenewalsIn26Months_AreOneYearlyStreamOfHighConfidence()
    {
        var rows = new[] { -25, -13, -1 }.Select(m => Row("Anuidade Exemplo", 120m, Today.AddMonths(m)));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(3, stream.Facts.Occurrences);
        Assert.Equal(RecurringConfidences.High, stream.Facts.Confidence);
    }

    /// <summary>What is older than 13 months is only read for the yearly cadence: it forms nothing else.</summary>
    [Fact]
    public void WhatIsOlderThan13Months_FormsNoMonthlyStream_NoInstalment_AndNoHabit()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, Today.AddMonths(-24), 8)                       // ended 17 months ago
            .Append(Row("LOJA 02/24", 150m, Today.AddMonths(-16)))
            .Append(Row("LOJA 03/24", 150m, Today.AddMonths(-15)))
            .Concat(Enumerable.Range(0, 6).Select(i => Row("Padaria Exemplo", 12.50m, Today.AddMonths(-20).AddDays(7 * i))));

        Assert.Empty(Detect(rows));
    }

    /// <summary>A monthly subscription that goes back 26 months is one monthly stream — its old charges do not make a yearly one.</summary>
    [Fact]
    public void AMonthlySubscriptionOf26Months_IsOneMonthlyStream_WithTheChargesOfTheLast13Months()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2024, 9, 5), 26);   // up to 2026-10-05

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringCadences.Monthly, stream.Cadence);
        Assert.Equal(13, stream.Facts.Occurrences);   // from 2025-09-20 on: 2025-10-05 to 2026-10-05
    }

    /// <summary>A yearly charge whose renewal is late (more than 395 days) is not shown as a yearly charge.</summary>
    [Fact]
    public void AYearlyChargeThatWasNotRenewed_IsNotFound()
    {
        var rows = new[]
        {
            Row("Anuidade Exemplo", 120m, Today.AddDays(-396 - 365)),
            Row("Anuidade Exemplo", 120m, Today.AddDays(-396)),
        };

        Assert.Empty(Detect(rows));
    }
}
