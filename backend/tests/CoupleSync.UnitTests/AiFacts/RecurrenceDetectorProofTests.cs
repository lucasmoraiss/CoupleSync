using CoupleSync.Application.AiFacts;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.AiFacts;

/// <summary>
/// Issue #39 — "proof proportional to the movement" (design 3.3): only what has proof counts in the total; a pattern
/// without proof is a "possible recurrence" (low confidence, shown apart, out of the total); a shopping habit is
/// nothing. One group of tests per rule: (a) the establishment only charges that, on its own day; (b) one price on
/// its own day in a shop that also has loose purchases; (c) instalments; (d) what the person said is recurring.
/// "Today" is always given; every establishment is made up.
/// </summary>
public sealed class RecurrenceDetectorProofTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);
    private static readonly Guid Ana = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bruno = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly RecurrenceOptions Options = new();
    private const string Shop = "Loja de Aplicativos Exemplo";

    private static RecurrenceRow Row(
        string merchant, decimal amount, DateOnly date, string category = "LAZER", Guid? user = null, TransactionSource source = TransactionSource.Notification)
        // 15:00 UTC is noon in Brasília: the local date is the date given.
        => new(Guid.NewGuid(), date.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), amount, merchant, null, category, user ?? Ana, source);

    private static IReadOnlyList<DetectedStream> Detect(IEnumerable<RecurrenceRow> rows, params IReadOnlyCollection<Guid>[] saidRecurring)
        => RecurrenceDetector.Detect(rows.ToList(), Today, Options, saidRecurring).Streams;

    private static bool IsConfirmed(DetectedStream stream) => stream.Facts.Confidence != RecurringConfidences.Low;

    /// <summary>What the total of the screen counts: a confirmed stream that is not a habit and did not stop.</summary>
    private static bool InTotal(DetectedStream stream)
        => IsConfirmed(stream) && stream.Facts.Kind != RecurringKinds.Habit && stream.Facts.Status != RecurringStatuses.Stopped;

    private static List<RecurrenceRow> On(string merchant, decimal amount, string category, params (int Month, int Day)[] dates)
        => dates.Select(d => Row(merchant, amount, new DateOnly(2026, d.Month, d.Day), category)).ToList();

    /// <summary>A subscription of R$ 29,90 on the 10th of each of the last <paramref name="charges"/> months (the last one in September).</summary>
    private static List<RecurrenceRow> Subscription(int charges, string merchant = Shop)
        => Enumerable.Range(0, charges).Select(i => Row(merchant, 29.90m, new DateOnly(2026, 9, 10).AddMonths(-(charges - 1 - i)))).ToList();

    /// <summary>
    /// Loose purchases in the same shop: <paramref name="perMonth"/> a month from January to September, on days that
    /// move, never near the 10th, each of another amount (R$ 61 to R$ 437): nothing in them repeats.
    /// </summary>
    private static List<RecurrenceRow> LoosePurchases(int perMonth, string merchant = Shop)
    {
        int[] days = [14, 17, 21, 24, 27, 15, 19, 23, 26];
        var rows = new List<RecurrenceRow>();
        for (var month = 1; month <= 9; month++)
        {
            for (var visit = 0; visit < perMonth; visit++)
            {
                var index = (month - 1) * perMonth + visit;
                var day = days[(month * 2 + visit * 2) % days.Length];
                rows.Add(Row(merchant, 61m + index * 9.4m, new DateOnly(2026, month, day)));
            }
        }

        return rows;
    }

    // ---------------------------------------------------------------- (a) the establishment only charges that

    [Theory]
    [InlineData("LAZER")]
    [InlineData("COMPRAS")]
    [InlineData("OUTROS")]
    [InlineData("ALIMENTACAO")]
    public void AnAmountThatVaries_WhereTheEstablishmentOnlyChargesThat_OnItsOwnDay_IsAConfirmedFixedBill_InAnyCategory(string category)
    {
        var rows = new[] { (8, 180m), (9, 230m), (10, 150m) }.Select(v => Row("Companhia Exemplo", v.Item2, new DateOnly(2026, v.Item1, 6), category));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringKinds.FixedBill, stream.Facts.Kind);
        Assert.True(stream.Facts.VariableAmount);
        Assert.Equal(RecurringConfidences.High, stream.Facts.Confidence);
        Assert.Equal(180m, stream.Facts.MedianAmount);
        Assert.True(InTotal(stream));
    }

    /// <summary>The window of "its own day" is 7 days of the month: the 1st to the 7th is on its day, the 1st to the 8th is not.</summary>
    [Theory]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void WhereTheEstablishmentOnlyChargesThat_TheDayDecides_ConfirmedInsideAWindowOfSevenDays_PossibleOutOfIt(int lastDay, bool confirmed)
    {
        var rows = On("Clube Exemplo", 39.90m, "LAZER", (6, 1), (7, 3), (8, 5), (9, lastDay));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringCadences.Monthly, stream.Cadence);
        Assert.Equal(confirmed ? RecurringConfidences.High : RecurringConfidences.Low, stream.Facts.Confidence);
        Assert.Equal(confirmed, InTotal(stream));
    }

    /// <summary>A name of the lists of subscriptions is proof enough when the establishment only charges that: the day may move.</summary>
    [Fact]
    public void AKnownSubscriptionName_WhereTheEstablishmentOnlyChargesThat_IsConfirmed_AlsoWhenTheDayMoves()
    {
        var stream = Assert.Single(Detect(On("Netflix", 39.90m, "LAZER", (6, 1), (7, 3), (8, 5), (9, 8))));

        Assert.Equal(RecurringConfidences.High, stream.Facts.Confidence);
        Assert.Equal(RecurringKinds.Subscription, stream.Facts.Kind);
    }

    [Fact]
    public void TheEndOfTheMonth_IsOneDay_The31stThe30thAndThe28thOfFebruary()
    {
        var rows = new[] { new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 30), new DateOnly(2026, 5, 31) }
            .Select(d => Row("Clube Exemplo", 39.90m, d));

        var stream = Assert.Single(RecurrenceDetector.Detect(rows.ToList(), new DateOnly(2026, 6, 8), Options).Streams);

        Assert.Equal(RecurringConfidences.High, stream.Facts.Confidence);
        Assert.Equal(5, stream.Facts.Occurrences);
    }

    [Fact]
    public void AWindowThatCrossesTheTurnOfTheMonth_IsStillOneDay()
    {
        // The 30th, the 1st, the 30th, the 1st: three days apart, not twenty-nine.
        var stream = Assert.Single(Detect(On("Clube Exemplo", 39.90m, "LAZER", (5, 30), (7, 1), (7, 30), (9, 1), (9, 30))));

        Assert.Equal(RecurringConfidences.High, stream.Facts.Confidence);
    }

    // ---------------------------------------------------------------- (b) a shop that also has loose purchases

    [Fact]
    public void TheLoosePurchasesOfTheFixtures_AloneFormNothing()
    {
        Assert.Empty(Detect(LoosePurchases(1)));
        Assert.Empty(Detect(LoosePurchases(4)));
    }

    /// <summary>One loose purchase a month next to the subscription: 5 charges of one price on their day are the proof; 3 and 4 are only possible.</summary>
    [Theory]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    public void InAShopWithLoosePurchases_OnePriceOnItsDay_IsConfirmedWithFiveCharges_AndPossibleBefore(int charges, bool confirmed)
    {
        var stream = Assert.Single(Detect(Subscription(charges).Concat(LoosePurchases(1))));

        Assert.Equal(29.90m, stream.Facts.MedianAmount);
        Assert.Equal(charges, stream.Facts.Occurrences);
        Assert.Equal(RecurringKinds.Subscription, stream.Facts.Kind);
        Assert.Equal(confirmed ? RecurringConfidences.High : RecurringConfidences.Low, stream.Facts.Confidence);
        Assert.Equal(confirmed, InTotal(stream));
    }

    /// <summary>
    /// A busy shop (more than two other days with a purchase for each charge) asks for one charge more: 6 to count,
    /// 4 to be shown as possible, and with 3 nothing is said.
    /// </summary>
    [Theory]
    [InlineData(3, null)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void InABusyShop_MoreProofIsAsked(int charges, bool? confirmed)
    {
        var streams = Detect(Subscription(charges).Concat(LoosePurchases(4)));

        if (confirmed is null)
        {
            Assert.Empty(streams);
            return;
        }

        var stream = Assert.Single(streams);
        Assert.Equal(29.90m, stream.Facts.MedianAmount);
        Assert.Equal(confirmed.Value ? RecurringConfidences.High : RecurringConfidences.Low, stream.Facts.Confidence);
    }

    /// <summary>The same price bought on other days too is a price of the shop, not a subscription: nothing goes to the total.</summary>
    [Fact]
    public void WhenThePriceIsAlsoSeenOutOfItsDay_NothingIsConfirmed()
    {
        // Six charges on the 10th would be the proof; the same R$ 29,90 on two other days of the period takes it away.
        var samePriceOnOtherDays = On(Shop, 29.90m, "LAZER", (6, 25), (8, 18));

        var streams = Detect(Subscription(6).Concat(LoosePurchases(1)).Concat(samePriceOnOtherDays));

        Assert.DoesNotContain(streams, InTotal);
        Assert.True(InTotal(Assert.Single(Detect(Subscription(6).Concat(LoosePurchases(1))))));
    }

    /// <summary>A possible recurrence says nothing yet: no "forgotten", no "got more expensive", no "new".</summary>
    [Theory]
    [InlineData(8, false)]
    [InlineData(7, true)]
    public void APossibleRecurrence_HasNoMarks(int lastDay, bool confirmed)
    {
        // Eight months of R$ 29,90 in an establishment that only charges that. On its day it is "maybe forgotten".
        var rows = On("Clube Exemplo", 29.90m, "LAZER", (2, 1), (3, 3), (4, 5), (5, 7), (6, 7), (7, 7), (8, 7), (9, lastDay), (10, 7));

        var stream = Assert.Single(Detect(rows));

        if (confirmed)
        {
            Assert.Equal(RecurringConfidences.High, stream.Facts.Confidence);
            Assert.Contains(RecurringFlags.Forgotten, stream.Facts.Flags);
        }
        else
        {
            Assert.Equal(RecurringConfidences.Low, stream.Facts.Confidence);
            Assert.Empty(stream.Facts.Flags);
        }
    }

    // ---------------------------------------------------------------- (c) instalments

    [Fact]
    public void ConsecutiveInstalments_AreConfirmed_AlsoInAShopFullOfOtherPurchases()
    {
        var parts = new[] { Row($"{Shop} 02/10", 150m, new DateOnly(2026, 8, 12), "COMPRAS"), Row($"{Shop} 03/10", 150m, new DateOnly(2026, 9, 12), "COMPRAS") };

        var stream = Assert.Single(Detect(parts.Concat(LoosePurchases(4))));

        Assert.Equal(RecurringKinds.Installment, stream.Facts.Kind);
        Assert.Equal(RecurringConfidences.High, stream.Facts.Confidence);
        Assert.Equal(150m * 7, stream.Facts.RemainingAmount);
    }

    /// <summary>The same part seen twice (the phone of each person, a statement and a notification) is one part.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnInstalmentRegisteredTwice_IsCountedOnce(bool anotherPerson, bool anotherMeans)
    {
        var copyUser = anotherPerson ? Bruno : Ana;
        var copySource = anotherMeans ? TransactionSource.Manual : TransactionSource.Notification;
        var rows = new[]
        {
            Row("LOJA 02/10", 150m, new DateOnly(2026, 9, 12), "COMPRAS"),
            Row("LOJA 03/10", 150m, new DateOnly(2026, 10, 5), "COMPRAS"),
            Row("LOJA 03/10", 150m, new DateOnly(2026, 10, 6), "COMPRAS", copyUser, copySource),
        };

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(150m, stream.Facts.MedianAmount);
        Assert.Equal(150m * 7, stream.Facts.RemainingAmount);
        Assert.Equal(3, stream.Facts.InstallmentNumber);
    }

    /// <summary>Two purchases of the same plan by the same person through the same means are two purchases: one commitment of both.</summary>
    [Fact]
    public void TwoPurchasesOfTheSamePlan_ByTheSamePersonAndMeans_AreSummed()
    {
        var rows = new[]
        {
            Row("LOJA 02/10", 150m, new DateOnly(2026, 9, 12), "COMPRAS"),
            Row("LOJA 03/10", 150m, new DateOnly(2026, 10, 5), "COMPRAS"),
            Row("LOJA 03/10", 150m, new DateOnly(2026, 10, 6), "COMPRAS"),
        };

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(300m, stream.Facts.MedianAmount);
        Assert.Equal(300m * 7, stream.Facts.RemainingAmount);
    }

    // ---------------------------------------------------------------- (d) what the person said is recurring

    [Fact]
    public void WhatThePersonSaidIsRecurring_IsConfirmed_WithoutAnyOtherProof()
    {
        var subscription = Subscription(4);
        var rows = subscription.Concat(LoosePurchases(4)).ToList();
        Assert.Equal(RecurringConfidences.Low, Assert.Single(Detect(rows)).Facts.Confidence);

        // The person said so when the stream had its first three charges; the fourth came later.
        var stream = Assert.Single(Detect(rows, subscription.Take(3).Select(r => r.Id).ToList()));

        Assert.Equal(RecurringConfidences.High, stream.Facts.Confidence);
        Assert.Equal(4, stream.Facts.Occurrences);
        Assert.True(InTotal(stream));
    }

    [Fact]
    public void WhatThePersonSaidOfAnotherStream_ConfirmsNothingElse()
    {
        var subscription = Subscription(4);
        var loose = LoosePurchases(4);

        // Charges of something else (three loose purchases), and one charge only of the series: not most of them.
        var stream = Assert.Single(Detect(
            subscription.Concat(loose),
            loose.Take(3).Select(r => r.Id).ToList(),
            new[] { subscription[0].Id, loose[5].Id, loose[6].Id }));

        Assert.Equal(RecurringConfidences.Low, stream.Facts.Confidence);
    }

    // ---------------------------------------------------------------- shopping habits: nothing in the total

    /// <summary>
    /// Forty made-up histories of each kind of shopping place (the generator of the bench, fixed seeds): a market, a
    /// restaurant, a fuel station, a bakery, a pharmacy (in SAUDE and in OUTROS), a ride app. None of them puts
    /// anything in the total.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void AShoppingPlace_PutsNothingInTheTotal(int profile)
    {
        var result = RecurrenceProofBench.MeasureProfile(profile, 39_100_000, 40, RecurrenceProofBench.Shape.Uniform, Options);

        Assert.True(result.ItemsInTotal == 0, $"{result.Label}: {result.ItemsInTotal} item(s) in the total in {result.WithItemInTotal} of {result.Histories} histories.");
    }

    // ---------------------------------------------------------------- what is truly recurring is confirmed

    /// <summary>Forty made-up histories of each kind of true recurrence, alone in its establishment: nearly all in the total (a bill whose day moves three days is sometimes out of the monthly rhythm, and is not found).</summary>
    [Theory]
    [InlineData(0, 38)]    // a subscription, one price, one day
    [InlineData(1, 38)]    // a subscription whose day moves two days
    [InlineData(2, 38)]    // a subscription on the last day of the month
    [InlineData(3, 38)]    // a subscription with a price increase
    [InlineData(5, 34)]    // a variable bill with a utility name, in MORADIA
    [InlineData(6, 34)]    // a variable bill with a generic name, in MORADIA
    [InlineData(7, 34)]    // a variable bill with a generic name, in OUTROS
    [InlineData(8, 38)]    // rent
    [InlineData(9, 38)]    // a fixed monthly fee
    [InlineData(10, 38)]   // a yearly charge
    [InlineData(12, 38)]   // instalments
    [InlineData(17, 38)]   // a subscription in a foreign currency
    [InlineData(21, 38)]   // a new subscription (3 charges)
    public void WhatIsTrulyRecurring_AloneInItsEstablishment_IsConfirmed(int scenario, int atLeast)
    {
        var result = RecurrenceProofBench.MeasureScenario(scenario, 39_200_000, 40, RecurrenceProofBench.Shape.Uniform, Options);

        Assert.True(result.Confirmed >= atLeast, $"{result.Label}: {result.Confirmed} confirmed, {result.Possible} possible, {result.NotFound} not found of {result.Histories}.");
        Assert.Equal(0, result.ExtraInTotal);
    }

    /// <summary>A subscription in a shop with loose purchases is never lost: confirmed or, while it has few charges, possible.</summary>
    [Fact]
    public void ASubscriptionInAShopWithLoosePurchases_IsConfirmedOrPossible()
    {
        var result = RecurrenceProofBench.MeasureScenario(4, 39_200_000, 40, RecurrenceProofBench.Shape.Uniform, Options);

        Assert.True(result.Confirmed + result.Possible >= 39, $"{result.Confirmed} confirmed, {result.Possible} possible, {result.NotFound} not found of {result.Histories}.");
        Assert.True(result.Confirmed >= 34, $"{result.Confirmed} confirmed of {result.Histories}.");
        Assert.Equal(0, result.ExtraInTotal);
    }

    // ---------------------------------------------------------------- I2: a step of price needs a series on one side

    /// <summary>Review 2, scenario A: a restaurant, 8 visits; 80, 82, 140, 138 on close days are not "a bill that got more expensive".</summary>
    [Fact]
    public void TwoPairsOfPurchasesOfTwoPrices_AreNotOneBillThatGotMoreExpensive()
    {
        (int Day, int Month, decimal Amount)[] visits =
        [
            (10, 1, 80m), (9, 2, 82m), (10, 3, 140m), (8, 4, 138m),
            (20, 1, 35m), (22, 2, 210m), (25, 3, 55m), (18, 4, 300m),
        ];
        var rows = visits.Select(v => Row("Restaurante Exemplo", v.Amount, new DateOnly(2026, v.Month, v.Day), "ALIMENTACAO")).ToList();

        Assert.Empty(RecurrenceDetector.Detect(rows, new DateOnly(2026, 4, 20), Options).Streams);
    }

    /// <summary>Review 2, scenario B: fuel every 10 days, 6 × R$ 100 then 6 × R$ 150. One amount alone forms nothing either.</summary>
    [Theory]
    [InlineData(150)]
    [InlineData(100)]
    public void APurchaseEveryTenDays_WhosePriceChanged_IsNotABill(int secondPrice)
    {
        var first = new DateOnly(2026, 1, 5);
        var rows = Enumerable.Range(0, 12).Select(i => Row("Posto Exemplo", i < 6 ? 100m : secondPrice, first.AddDays(10 * i), "TRANSPORTE")).ToList();

        Assert.Empty(RecurrenceDetector.Detect(rows, first.AddDays(10 * 11 + 2), Options).Streams);
    }
}
