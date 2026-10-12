using CoupleSync.Application.AiFacts;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.AiFacts;

/// <summary>
/// Issue #39, review round 1 — what the first review found missing in the detector: two services of the same
/// establishment and amount, the mark "new", how long "got more expensive" stays, texts longer than the columns,
/// and what a name that comes from a description may carry. "Today" is always given; establishments are made up.
/// </summary>
public sealed class RecurrenceDetectorReviewTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static readonly Guid Ana = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bruno = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly RecurrenceOptions Options = new();

    private static RecurrenceRow Row(string? merchant, decimal amount, DateOnly date, string category = "LAZER", string? description = null, Guid? user = null)
        // 15:00 UTC is noon in Brasília: the local date is the date given.
        => new(Guid.NewGuid(), date.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), amount, merchant, description, category, user ?? Ana, TransactionSource.Manual);

    private static List<RecurrenceRow> Monthly(string? merchant, decimal amount, DateOnly first, int count, string category = "LAZER", string? description = null, Guid? user = null)
        => Enumerable.Range(0, count).Select(i => Row(merchant, amount, first.AddMonths(i), category, description, user)).ToList();

    private static IReadOnlyList<DetectedStream> Detect(IEnumerable<RecurrenceRow> rows, DateOnly? today = null)
        => RecurrenceDetector.Detect(rows.ToList(), today ?? Today, Options).Streams;

    // ---------------------------------------------------------------- I2: the same amount, twice, in the same establishment

    /// <summary>Each person of the couple pays the same plan of the same service, on different days.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void TheSamePlanPaidByEachPerson_OnDifferentDays_IsTwoStreams_EachWithItsPerson(int months)
    {
        var firstMonth = new DateOnly(2026, 10, 1).AddMonths(-(months - 1));
        var rows = Monthly("Musica Exemplo", 21.90m, firstMonth.AddDays(2), months, user: Ana)        // day 3
            .Concat(Monthly("Musica Exemplo", 21.90m, firstMonth.AddDays(16), months, user: Bruno))   // day 17
            .ToList();

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.Equal("musica exemplo", s.MerchantKey));
        Assert.All(streams, s => Assert.Equal(RecurringCadences.Monthly, s.Cadence));
        Assert.All(streams, s => Assert.Equal(RecurringKinds.Subscription, s.Facts.Kind));
        Assert.All(streams, s => Assert.Equal(months, s.Facts.Occurrences));
        Assert.All(streams, s => Assert.Equal(21.90m, s.Facts.MedianAmount));
        Assert.Equal(new Guid?[] { Ana, Bruno }, streams.Select(s => s.Facts.UserId).Order());
        // No charge is in both.
        Assert.Equal(2 * months, streams.SelectMany(s => s.TransactionIds).Distinct().Count());
    }

    /// <summary>The wide band (a health plan of each person, SAUDE) goes through the same rule.</summary>
    [Fact]
    public void TheSameFixedBillOfEachPerson_IsTwoStreams()
    {
        var rows = Monthly("Plano Exemplo", 320m, new DateOnly(2026, 6, 4), 5, "SAUDE", user: Ana)
            .Concat(Monthly("Plano Exemplo", 320m, new DateOnly(2026, 6, 19), 5, "SAUDE", user: Bruno));

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.Equal(RecurringKinds.FixedBill, s.Facts.Kind));
        Assert.Equal(new Guid?[] { Ana, Bruno }, streams.Select(s => s.Facts.UserId).Order());
    }

    /// <summary>Two subscriptions of the same price in the same app shop, of the same person, each on its own day.</summary>
    [Fact]
    public void TwoSubscriptionsOfTheSamePrice_OfTheSamePerson_EachOnItsDay_AreTwoStreams()
    {
        var rows = Monthly("APPLE.COM/BILL", 21.90m, new DateOnly(2026, 6, 3), 5).Concat(Monthly("APPLE.COM/BILL", 21.90m, new DateOnly(2026, 6, 17), 5));

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.Equal(5, s.Facts.Occurrences));
        Assert.Equal([3, 17], streams.Select(s => s.Facts.FirstSeenLocal.Day).Order());
    }

    /// <summary>What must keep being refused: a purchase every 10 or every 14 days is not two monthly charges.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(14)]
    public void APurchaseEveryFewDays_IsStillNotMonthly(int everyDays)
    {
        var rows = Enumerable.Range(0, 14).Select(i => Row("Posto Exemplo", 200m, Today.AddDays(-everyDays * i), "TRANSPORTE"));

        Assert.DoesNotContain(Detect(rows), s => s.Cadence == RecurringCadences.Monthly);
    }

    /// <summary>One subscription whose charges were registered first by one person and then by the other stays one stream.</summary>
    [Fact]
    public void OneSubscriptionRegisteredByOnePersonAndThenByTheOther_IsOneStream()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 5, 5), 3, user: Ana)
            .Concat(Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 8, 5), 3, user: Bruno));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(6, stream.Facts.Occurrences);
        Assert.Null(stream.Facts.UserId);
    }

    // ---------------------------------------------------------------- decision 3: the mark "new"

    [Fact]
    public void AMonthlySubscriptionThatJustGotItsThirdCharge_IsNew()
    {
        var stream = Assert.Single(Detect(Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 8, 18), 3)));

        Assert.Contains(RecurringFlags.New, stream.Facts.Flags);
    }

    /// <summary>The mark goes by the first known charge of the stream, in Brasília days, and goes away by itself.</summary>
    [Theory]
    [InlineData(90, true)]
    [InlineData(91, false)]
    public void TheMarkNew_LastsWhileTheFirstChargeIsRecent(int daysSinceFirst, bool isNew)
    {
        var first = new DateOnly(2026, 7, 1);
        var stream = Assert.Single(Detect(Monthly("Streaming Exemplo", 39.90m, first, 4), first.AddDays(daysSinceFirst)));

        Assert.Equal(isNew, stream.Facts.Flags.Contains(RecurringFlags.New));
    }

    [Fact]
    public void AnOldSubscription_IsNotNew()
    {
        var stream = Assert.Single(Detect(Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 3, 18), 8)));

        Assert.DoesNotContain(RecurringFlags.New, stream.Facts.Flags);
    }

    // ---------------------------------------------------------------- m3: how long "got more expensive" stays

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void GotMoreExpensive_IsShownOnlyWhileTheNewPriceIsRecent(int chargesAtTheNewPrice, bool marked)
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 3, 5), 3)
            .Concat(Monthly("Streaming Exemplo", 44.90m, new DateOnly(2026, 6, 5), chargesAtTheNewPrice));

        var stream = Assert.Single(Detect(rows, new DateOnly(2026, 6, 6).AddMonths(chargesAtTheNewPrice - 1)));

        Assert.Equal(marked, stream.Facts.Flags.Contains(RecurringFlags.PriceIncrease));
        // The price before stays as a fact of the item.
        Assert.Equal(39.90m, stream.Facts.PreviousAmount);
        Assert.Equal(44.90m, stream.Facts.MedianAmount);
    }

    // ---------------------------------------------------------------- I3: texts longer than the columns

    [Fact]
    public void TheKeyOfAVeryLongText_IsCutAtAWholeWord_AndLeavesRoomForTheSuffixes()
    {
        var text = string.Join(' ', Enumerable.Range(0, 80).Select(i => $"palavra{(char)('a' + i % 26)}"));   // 719 letters
        Assert.True(text.Length > 512);

        var key = MerchantKey.Normalize(text);

        Assert.True(key.Length <= MerchantKey.MaxLength, $"the key has {key.Length} characters");
        // "#48x202609" of an instalment plan and "~99" of a second stream still fit in the column.
        Assert.True(MerchantKey.MaxLength + "#48x202609".Length + "~99".Length <= RecurringStream.MaxMerchantKeyLength);
        Assert.StartsWith("palavraa palavrab", key, StringComparison.Ordinal);
        Assert.All(key.Split(' '), word => Assert.StartsWith("palavra", word, StringComparison.Ordinal));
        Assert.Equal(8, key.Split(' ')[^1].Length);
        // The same text always gives the same key, and so does a longer text with the same beginning.
        Assert.Equal(key, MerchantKey.Normalize(text + " mais texto"));
    }

    [Fact]
    public void AOneWordTextLongerThanTheKey_IsCutToTheLength()
    {
        var key = MerchantKey.Normalize(new string('a', 400));

        Assert.Equal(MerchantKey.MaxLength, key.Length);
    }

    [Fact]
    public void TheNameShown_NeverEndsInHalfACharacter()
    {
        // The emoji (two UTF-16 units) sits right on the limit of the name.
        var merchant = new string('a', RecurringStream.MaxDisplayNameLength - 1) + "\U0001F600 resto";

        var stream = Assert.Single(Detect(Monthly(merchant, 39.90m, new DateOnly(2026, 8, 5), 3)));

        var name = stream.Facts.DisplayName;
        Assert.True(name.Length <= RecurringStream.MaxDisplayNameLength);
        Assert.False(char.IsHighSurrogate(name[^1]), "the name was cut in the middle of a surrogate pair");
        Assert.Equal(new string('a', RecurringStream.MaxDisplayNameLength - 1), name);
    }

    // ---------------------------------------------------------------- decision 1: a name that comes from a description

    /// <summary>A document, a phone, an e-mail or a random Pix key in the description: the charge forms no stream at all.</summary>
    [Theory]
    [InlineData("Mensalidade Escola Exemplo 123.456.789-09")]
    [InlineData("Mensalidade Escola Exemplo 12345678909")]
    [InlineData("Boleto Exemplo Ltda 12.345.678/0001-95")]
    [InlineData("Aula Exemplo (11) 91234-5678")]
    [InlineData("Aula Exemplo contato@example.com")]
    [InlineData("Aula Exemplo 123e4567-e89b-12d3-a456-426614174000")]
    public void ADescriptionWithADocumentOrAContact_FormsNoStream(string description)
    {
        var result = RecurrenceDetector.Detect(Monthly(null, 150m, new DateOnly(2026, 6, 5), 5, "OUTROS", description), Today, Options);

        Assert.Empty(result.Streams);
        Assert.Empty(result.ChargesByKey);
    }

    /// <summary>A long number that is none of those (an authorization, a contract) only leaves the name.</summary>
    [Fact]
    public void ALongNumberInADescription_IsTakenOutOfTheName()
    {
        var stream = Assert.Single(Detect(Monthly(null, 150m, new DateOnly(2026, 6, 5), 5, "OUTROS", "Academia Exemplo 12345678 mensal")));

        Assert.Equal("academia exemplo mensal", stream.MerchantKey);
        Assert.Equal("Academia Exemplo mensal", stream.Facts.DisplayName);
    }

    /// <summary>The merchant is the name of the shop as the bank wrote it: it is not rewritten here.</summary>
    [Fact]
    public void TheSameTextInTheMerchant_IsShownAsItIs()
    {
        var stream = Assert.Single(Detect(Monthly("Academia Exemplo 12345678", 150m, new DateOnly(2026, 6, 5), 5, "OUTROS")));

        Assert.Equal("Academia Exemplo 12345678", stream.Facts.DisplayName);
    }

    [Theory]
    [InlineData("MARIA S SILVA")]
    [InlineData("Pix enviado Zuleide")]
    [InlineData("mesada para a filha")]
    [InlineData("Mensalidade para Zuleide")]
    public void APersonOrATransferInADescriptionUsedAsTheName_FormsNoStream(string description)
        => Assert.Empty(Detect(Monthly(null, 150m, new DateOnly(2026, 6, 5), 5, "OUTROS", description)));

    // ---------------------------------------------------------------- decision 5: "para" in a free note

    /// <summary>With a merchant, a note like "plano para a família" is not a transfer: the name shown is the merchant.</summary>
    [Theory]
    [InlineData("plano para a família")]
    [InlineData("ração para o cachorro")]
    [InlineData("presente para as crianças")]
    public void ANoteWithParaFollowedByAnArticle_DoesNotHideAMerchant(string note)
    {
        var stream = Assert.Single(Detect(Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 8, 18), 3, description: note)));

        Assert.Equal("Streaming Exemplo", stream.Facts.DisplayName);
    }

    /// <summary>"para" followed by a name is still a transfer, wherever it is written.</summary>
    [Theory]
    [InlineData("Loja Exemplo", "para Zuleide")]
    [InlineData("Loja Exemplo", "enviado para a Zuleide")]
    [InlineData("Pagamento para Zuleide", null)]
    public void ParaFollowedByAName_IsStillATransfer(string merchant, string? description)
        => Assert.Empty(Detect(Monthly(merchant, 150m, new DateOnly(2026, 6, 5), 5, "OUTROS", description)));
}
