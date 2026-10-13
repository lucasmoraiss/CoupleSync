using CoupleSync.Application.AiFacts;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.AiFacts;

/// <summary>
/// Issue #39, review round 3 — what "the same charge seen twice" is, exactly: the same amount, a few days apart at
/// most, and not registered twice by the same person through the same means. Two series are one only when every
/// charge of one of them has its pair in the other. "Today" is always given; establishments and numbers are made up.
/// </summary>
public sealed class RecurrenceDetectorReview3Tests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static readonly Guid Ana = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bruno = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Carla = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly RecurrenceOptions Options = new();

    private static RecurrenceRow Row(string merchant, decimal amount, DateOnly date, string category = "LAZER", Guid? user = null, TransactionSource source = TransactionSource.Notification)
        // 15:00 UTC is noon in Brasília: the local date is the date given.
        => new(Guid.NewGuid(), date.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), amount, merchant, null, category, user ?? Ana, source);

    /// <summary>One charge a month from June 2026 on, on the day given for each month.</summary>
    private static List<RecurrenceRow> OnDays(string merchant, decimal amount, int[] days, string category = "LAZER", Guid? user = null, TransactionSource source = TransactionSource.Notification)
        => days.Select((day, i) => Row(merchant, amount, new DateOnly(2026, 6 + i, day), category, user, source)).ToList();

    private static IReadOnlyList<DetectedStream> Detect(IEnumerable<RecurrenceRow> rows)
        => RecurrenceDetector.Detect(rows.ToList(), Today, Options).Streams;

    // ---------------------------------------------------------------- D1.1: one close pair does not join two series

    /// <summary>
    /// The plan of each person, on the 5th and on the 10th. In one month the second one is charged on the 9th, four
    /// days from the first: they are still two streams, and the total is the two of them.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void OneChargeOfAPlanCloseToTheOtherPlan_DoesNotMakeThemOneStream(int monthOfTheCloseCharge)
    {
        var days = new[] { 10, 10, 10, 10, 10 };
        days[monthOfTheCloseCharge] = 9;
        var rows = OnDays("Streaming Exemplo", 39.90m, [5, 5, 5, 5, 5], user: Ana)
            .Concat(OnDays("Streaming Exemplo", 39.90m, days, user: Bruno));

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.Equal(new Guid?[] { Ana, Bruno }, streams.Select(s => s.Facts.UserId).Order());
        Assert.All(streams, s => Assert.Equal(5, s.Facts.Occurrences));
        Assert.All(streams, s => Assert.Empty(s.CopyTransactionIds));
        Assert.Equal(957.60m, streams.Sum(s => s.Facts.AnnualCost));
    }

    /// <summary>
    /// The same charge registered by both, and one of them also has a purchase of the same amount in the middle of a
    /// month: one stream, and only the charges next to its charges are its copies.
    /// </summary>
    [Fact]
    public void WhenTwoSeriesAreOne_OnlyTheChargesNextToItsChargesAreCopies()
    {
        var ofAna = OnDays("Streaming Exemplo", 39.90m, [5, 5, 5, 5, 5], user: Ana);
        var ofBruno = OnDays("Streaming Exemplo", 39.90m, [6, 6, 6, 6, 6], user: Bruno);
        var purchase = Row("Streaming Exemplo", 39.90m, new DateOnly(2026, 8, 20), user: Bruno);

        var stream = Assert.Single(Detect(ofAna.Concat(ofBruno).Append(purchase)));

        Assert.Equal(5, stream.Facts.Occurrences);
        Assert.Equal(478.80m, stream.Facts.AnnualCost);
        Assert.Equal(5, stream.CopyTransactionIds.Count);
        Assert.DoesNotContain(purchase.Id, stream.CopyTransactionIds);
        Assert.DoesNotContain(purchase.Id, stream.TransactionIds);
        // Every charge of the two series is either a charge of the stream or a copy of one.
        Assert.Equal(
            ofAna.Concat(ofBruno).Select(r => r.Id).Order(),
            stream.TransactionIds.Concat(stream.CopyTransactionIds).Order());
    }

    /// <summary>The second phone caught four of the six charges (not the ones in a row): still one stream.</summary>
    [Fact]
    public void TheSameCharge_CaughtInFourOfSixMonthsByTheOtherPerson_IsOneStream()
    {
        var ofAna = Enumerable.Range(5, 6).Select(month => Row("Streaming Exemplo", 39.90m, new DateOnly(2026, month, 5), user: Ana)).ToList();
        var ofBruno = new[] { 5, 6, 8, 9 }.Select(month => Row("Streaming Exemplo", 39.90m, new DateOnly(2026, month, 6), user: Bruno)).ToList();

        var stream = Assert.Single(Detect(ofAna.Concat(ofBruno)));

        Assert.Equal(6, stream.Facts.Occurrences);
        Assert.Equal(478.80m, stream.Facts.AnnualCost);
        Assert.Null(stream.Facts.UserId);
    }

    /// <summary>
    /// Three people: two of them register the same charge, the third one has a plan of their own on another day.
    /// Two streams — the shared one, of nobody in particular, and the one of the third person.
    /// </summary>
    [Fact]
    public void TheSameChargeOfTwoPeople_AndThePlanOfAThirdOne_AreTwoStreams()
    {
        var rows = OnDays("Streaming Exemplo", 39.90m, [5, 5, 5, 5, 5], user: Ana)
            .Concat(OnDays("Streaming Exemplo", 39.90m, [6, 6, 6, 6, 6], user: Bruno))
            .Concat(OnDays("Streaming Exemplo", 39.90m, [20, 20, 20, 20, 20], user: Carla));

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.Equal(new Guid?[] { null, Carla }, streams.Select(s => s.Facts.UserId).Order());
        Assert.All(streams, s => Assert.Equal(5, s.Facts.Occurrences));
        Assert.Equal(957.60m, streams.Sum(s => s.Facts.AnnualCost));
    }

    // ---------------------------------------------------------------- D1.2: a copy has the same amount

    /// <summary>
    /// Two bills of the same establishment, R$ 180 on the 10th and R$ 230 on the 12th: inside the band of a bill
    /// whose amount varies, and two days apart — but not the same amount. They are two bills, not one seen twice.
    /// </summary>
    [Theory]
    [InlineData(true)]    // both of the same person, one from the statement and one from the notification
    [InlineData(false)]   // one of each person
    public void TwoBillsOfDifferentAmounts_TwoDaysApart_AreNotOneChargeSeenTwice(bool samePerson)
    {
        var rows = OnDays("Condominio Exemplo", 180m, [10, 10, 10, 10, 10], "MORADIA", Ana, TransactionSource.Notification)
            .Concat(OnDays("Condominio Exemplo", 230m, [12, 12, 12, 12, 12], "MORADIA", samePerson ? Ana : Bruno, samePerson ? TransactionSource.OcrImport : TransactionSource.Notification));

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.Equal(new[] { 180m, 230m }, streams.Select(s => s.Facts.MedianAmount).Order());
        Assert.All(streams, s => Assert.Equal(5, s.Facts.Occurrences));
        Assert.All(streams, s => Assert.Empty(s.CopyTransactionIds));
        Assert.Equal(4920m, streams.Sum(s => s.Facts.AnnualCost));
    }

    // ---------------------------------------------------------------- D1.3: the same person, the same means

    /// <summary>
    /// Two subscriptions of the same price, of the same person, registered the same way, on the 3rd and on the 6th:
    /// the same phone does not capture one charge twice on different days. Two streams.
    /// </summary>
    [Theory]
    [InlineData(TransactionSource.Notification)]
    [InlineData(TransactionSource.Manual)]
    [InlineData(TransactionSource.OcrImport)]
    public void TwoSubscriptionsOfTheSamePerson_RegisteredTheSameWay_ThreeDaysApart_AreTwoStreams(TransactionSource source)
    {
        var rows = OnDays("APPLE.COM/BILL", 21.90m, [3, 3, 3, 3, 3], source: source)
            .Concat(OnDays("APPLE.COM/BILL", 21.90m, [6, 6, 6, 6, 6], source: source));

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.Equal(5, s.Facts.Occurrences));
        Assert.All(streams, s => Assert.Equal(Ana, s.Facts.UserId));
        Assert.All(streams, s => Assert.Empty(s.CopyTransactionIds));
        Assert.Equal(525.60m, streams.Sum(s => s.Facts.AnnualCost));
        Assert.Equal(new[] { 3, 6 }, streams.Select(s => s.Facts.LastSeenLocal.Day).Order());
    }

    /// <summary>
    /// The other side: the same person has the charge from the statement and from the notification, one to four
    /// days apart. It is one charge seen twice: one stream, of that person.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheSameChargeOfOnePerson_FromTheStatementAndFromTheNotification_IsOneStream(int daysApart)
    {
        var rows = OnDays("Streaming Exemplo", 39.90m, [5, 5, 5, 5, 5], source: TransactionSource.Notification)
            .Concat(OnDays("Streaming Exemplo", 39.90m, Enumerable.Repeat(5 + daysApart, 5).ToArray(), source: TransactionSource.OcrImport));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(5, stream.Facts.Occurrences);
        Assert.Equal(478.80m, stream.Facts.AnnualCost);
        Assert.Equal(Ana, stream.Facts.UserId);
        Assert.Equal(5, stream.CopyTransactionIds.Count);
    }

    /// <summary>
    /// Not reopened (M4): a purchase of the same amount twice a month, of the same person and registered the same
    /// way, on days that move, is still not two fixed bills.
    /// </summary>
    [Fact]
    public void APurchaseTwiceAMonthOnDaysThatMove_OfTheSamePerson_IsStillNotTwoFixedBills()
    {
        var rows = OnDays("Posto Exemplo", 200m, [5, 8, 4, 7, 5], "TRANSPORTE")
            .Concat(OnDays("Posto Exemplo", 200m, [20, 17, 22, 19, 21], "TRANSPORTE"));

        var streams = Detect(rows);

        Assert.True(streams.Count <= 1, $"{streams.Count} streams");
        Assert.True(streams.Sum(s => s.Facts.AnnualCost) <= 2400m);
    }

    /// <summary>
    /// Not reopened (M4): on days that are close and move (around the 3rd and around the 7th), no pair of series
    /// each on its own day exists — it is not two fixed bills either.
    /// </summary>
    [Fact]
    public void APurchaseTwiceAMonthOnCloseDaysThatMove_IsNotTwoFixedBills()
    {
        var rows = OnDays("Posto Exemplo", 200m, [3, 1, 5, 2, 4], "TRANSPORTE")
            .Concat(OnDays("Posto Exemplo", 200m, [7, 9, 8, 10, 7], "TRANSPORTE"));

        Assert.True(Detect(rows).Count <= 1);
    }
}
