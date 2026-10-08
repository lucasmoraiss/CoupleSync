using CoupleSync.Application.AiFacts;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.AiFacts;

/// <summary>
/// Issue #39 — the recurrence detector (design 3.3, 3.4 and 3.2) on synthetic transactions. "Today" is always given:
/// nothing here reads the clock of the machine. Establishments are made up or generic.
/// </summary>
public sealed class RecurrenceDetectorTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);
    private static readonly Guid Ana = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bruno = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly RecurrenceOptions Options = new();

    private static RecurrenceRow Row(string? merchant, decimal amount, DateOnly date, string category = "LAZER", string? description = null, Guid? user = null)
        // 15:00 UTC is noon in Brasília: the local date is the date given.
        => new(Guid.NewGuid(), date.ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc), amount, merchant, description, category, user ?? Ana, TransactionSource.Manual);

    private static List<RecurrenceRow> Series(string merchant, decimal amount, DateOnly first, int count, int everyDays, string category = "LAZER")
        => Enumerable.Range(0, count).Select(i => Row(merchant, amount, first.AddDays(i * everyDays), category)).ToList();

    private static List<RecurrenceRow> Monthly(string merchant, decimal amount, DateOnly first, int count, string category = "LAZER")
        => Enumerable.Range(0, count).Select(i => Row(merchant, amount, first.AddMonths(i), category)).ToList();

    private static IReadOnlyList<DetectedStream> Detect(IEnumerable<RecurrenceRow> rows, DateOnly? today = null)
        => RecurrenceDetector.Detect(rows.ToList(), today ?? Today, Options).Streams;

    // ---------------------------------------------------------------- cadence and occurrences

    [Fact]
    public void ThreeMonthlyChargesOfTheSameAmount_AreAnActiveMonthlySubscription()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 8, 5), 3);

        var stream = Assert.Single(Detect(rows));

        Assert.Equal("streaming exemplo", stream.MerchantKey);
        Assert.Equal("Streaming Exemplo", stream.Facts.DisplayName);
        Assert.Equal(RecurringKinds.Subscription, stream.Facts.Kind);
        Assert.Equal(RecurringCadences.Monthly, stream.Cadence);
        Assert.Equal(RecurringStatuses.Active, stream.Facts.Status);
        Assert.Equal(39.90m, stream.Facts.MedianAmount);
        Assert.Equal(478.80m, stream.Facts.AnnualCost);
        Assert.Equal(3, stream.Facts.Occurrences);
        Assert.Equal(0, stream.Facts.MissedCount);
        Assert.Equal(new DateOnly(2026, 8, 5), stream.Facts.FirstSeenLocal);
        Assert.Equal(new DateOnly(2026, 10, 5), stream.Facts.LastSeenLocal);
        Assert.Equal(new DateOnly(2026, 11, 5), stream.Facts.NextExpectedLocal);
        Assert.Equal(RecurringConfidences.High, stream.Facts.Confidence);
        Assert.False(stream.Facts.VariableAmount);
        Assert.Equal(Ana, stream.Facts.UserId);
        Assert.Equal(rows.Select(r => r.Id).Order(), stream.TransactionIds.Order());
    }

    [Fact]
    public void TwoMonthlyCharges_AreNotEnough()
        => Assert.Empty(Detect(Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 9, 5), 2)));

    [Fact]
    public void TwoChargesSixtyDaysApart_AreNotDetected()
        => Assert.Empty(Detect(Series("Streaming Exemplo", 39.90m, new DateOnly(2026, 8, 5), 2, 60)));

    [Fact]
    public void AMonthWithoutACharge_DoesNotBreakTheSeries_AndIsCounted()
    {
        var rows = new[] { new DateOnly(2026, 6, 5), new DateOnly(2026, 7, 5), new DateOnly(2026, 9, 5), new DateOnly(2026, 10, 5) }
            .Select(d => Row("Streaming Exemplo", 39.90m, d));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(4, stream.Facts.Occurrences);
        Assert.Equal(1, stream.Facts.MissedCount);
        Assert.Equal(new DateOnly(2026, 6, 5), stream.Facts.FirstSeenLocal);
        Assert.Equal(RecurringStatuses.Active, stream.Facts.Status);
    }

    [Fact]
    public void TwoMonthsWithoutACharge_BreakTheSeries()
    {
        var old = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 5, 5), 3);   // May, June, July
        var back = Row("Streaming Exemplo", 39.90m, new DateOnly(2026, 10, 5));        // August and September missing

        var stream = Assert.Single(Detect(old.Append(back)));

        // The charge of October did not continue the series: what was found is the old one, which stopped.
        Assert.Equal(3, stream.Facts.Occurrences);
        Assert.Equal(new DateOnly(2026, 7, 5), stream.Facts.LastSeenLocal);
        Assert.Equal(RecurringStatuses.Stopped, stream.Facts.Status);
        Assert.DoesNotContain(back.Id, stream.TransactionIds);
    }

    [Fact]
    public void TwoSkippedChargesInARow_BreakTheSeries()
    {
        // March, April, (May), June, (July), August, September, October.
        var dates = new[] { (3, 5), (4, 5), (6, 5), (8, 5), (9, 5), (10, 5) }.Select(d => new DateOnly(2026, d.Item1, d.Item2));

        var rows = dates.Select(d => Row("Streaming Exemplo", 39.90m, d)).ToList();

        var stream = Assert.Single(Detect(rows));

        // One series cannot hold the two gaps: it starts again in June, with only the gap of July in it.
        Assert.Equal(4, stream.Facts.Occurrences);
        Assert.Equal(new DateOnly(2026, 6, 5), stream.Facts.FirstSeenLocal);
        Assert.Equal(1, stream.Facts.MissedCount);
        Assert.DoesNotContain(rows[0].Id, stream.TransactionIds);
        Assert.DoesNotContain(rows[1].Id, stream.TransactionIds);
    }

    [Fact]
    public void ThreeWeeklyCharges_AreNotDetected_AndFourAreAWeeklyHabit()
    {
        Assert.Empty(Detect(Series("Feira Exemplo", 80m, new DateOnly(2026, 9, 22), 3, 7, "ALIMENTACAO")));

        var stream = Assert.Single(Detect(Series("Feira Exemplo", 80m, new DateOnly(2026, 9, 15), 4, 7, "ALIMENTACAO")));

        Assert.Equal(RecurringKinds.Habit, stream.Facts.Kind);
        Assert.Equal(RecurringCadences.Weekly, stream.Cadence);
        Assert.Equal(4, stream.Facts.Occurrences);
        Assert.Equal(80m * 52, stream.Facts.AnnualCost);
        Assert.Equal(new DateOnly(2026, 10, 13), stream.Facts.NextExpectedLocal);
    }

    [Fact]
    public void TwoChargesAYearApart_AreAYearlyStreamOfMediumConfidence()
    {
        var rows = new[] { Row("Seguro Exemplo", 1200m, new DateOnly(2025, 9, 20), "TRANSPORTE"), Row("Seguro Exemplo", 1250m, new DateOnly(2026, 9, 22), "TRANSPORTE") };

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringCadences.Yearly, stream.Cadence);
        Assert.Equal(RecurringConfidences.Medium, stream.Facts.Confidence);
        Assert.Equal(RecurringKinds.FixedBill, stream.Facts.Kind);
        Assert.Equal(stream.Facts.MedianAmount, stream.Facts.AnnualCost);
        Assert.Equal(new DateOnly(2027, 9, 22), stream.Facts.NextExpectedLocal);
    }

    /// <summary>A purchase every 10 days is not "monthly" just because every third one is 30 days apart.</summary>
    [Fact]
    public void ADenserHabit_IsNotReadAsAMonthlyCharge()
        => Assert.Empty(Detect(Series("Posto Exemplo", 200m, new DateOnly(2026, 6, 1), 13, 10, "TRANSPORTE")));

    [Fact]
    public void AnExtraPurchaseInTheSameShop_DoesNotBreakTheSeries()
    {
        var rows = Monthly("Loja Exemplo", 19.90m, new DateOnly(2026, 6, 5), 5);
        rows.Add(Row("Loja Exemplo", 250m, new DateOnly(2026, 8, 17)));
        rows.Add(Row("Loja Exemplo", 19.90m, new DateOnly(2026, 9, 14)));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(5, stream.Facts.Occurrences);
        Assert.Equal(19.90m, stream.Facts.MedianAmount);
    }

    [Fact]
    public void TwoSubscriptionsOfTheSameEstablishment_AreTwoStreams()
    {
        var rows = Monthly("APPLE.COM/BILL", 3.50m, new DateOnly(2026, 6, 3), 5).Concat(Monthly("APPLE.COM/BILL", 21.90m, new DateOnly(2026, 6, 18), 4));

        var streams = Detect(rows);

        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.Equal("apple bill", s.MerchantKey));
        Assert.All(streams, s => Assert.Equal(RecurringKinds.Subscription, s.Facts.Kind));
        Assert.Equal([3.50m, 21.90m], streams.Select(s => s.Facts.MedianAmount).Order());
    }

    // ---------------------------------------------------------------- the two bands of amount

    [Fact]
    public void AnElectricityBillThatVaries_IsAVariableFixedBill_ForecastByTheMedianOfTheLastThree()
    {
        var rows = new[]
        {
            Row("Companhia Exemplo", 210m, new DateOnly(2026, 7, 10), "MORADIA"),
            Row("Companhia Exemplo", 180m, new DateOnly(2026, 8, 10), "MORADIA"),
            Row("Companhia Exemplo", 230m, new DateOnly(2026, 9, 10), "MORADIA"),
            Row("Companhia Exemplo", 150m, new DateOnly(2026, 10, 6), "MORADIA"),
        };

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringKinds.FixedBill, stream.Facts.Kind);
        Assert.True(stream.Facts.VariableAmount);
        Assert.Equal(RecurringCadences.Monthly, stream.Cadence);
        Assert.Equal(180m, stream.Facts.MedianAmount);   // median of 180, 230, 150 — not of the four
        Assert.Equal(150m, stream.Facts.LastAmount);
        Assert.Equal(180m * 12, stream.Facts.AnnualCost);
        Assert.Null(stream.Facts.PreviousAmount);
        Assert.DoesNotContain(RecurringFlags.PriceIncrease, stream.Facts.Flags);
    }

    [Fact]
    public void TheExactValuesOfTheIssue_180_230_150_InMoradia_AreAVariableFixedBill()
    {
        var rows = new[] { (8, 180m), (9, 230m), (10, 150m) }.Select(v => Row("Companhia Exemplo", v.Item2, new DateOnly(2026, v.Item1, 6), "MORADIA"));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringKinds.FixedBill, stream.Facts.Kind);
        Assert.True(stream.Facts.VariableAmount);
        Assert.Equal(180m, stream.Facts.MedianAmount);
    }

    [Fact]
    public void TheSameValuesInLazer_DoNotFormASeries()
    {
        var rows = new[] { (8, 180m), (9, 230m), (10, 150m) }.Select(v => Row("Companhia Exemplo", v.Item2, new DateOnly(2026, v.Item1, 6), "LAZER"));

        Assert.Empty(Detect(rows));
    }

    [Fact]
    public void AUtilityHint_AlsoOpensTheWideBand_WhateverTheCategory()
    {
        var rows = new[] { (8, 180m), (9, 230m), (10, 150m) }.Select(v => Row("Conta de luz", v.Item2, new DateOnly(2026, v.Item1, 6), "OUTROS"));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringKinds.FixedBill, stream.Facts.Kind);
        Assert.True(stream.Facts.VariableAmount);
    }

    [Fact]
    public void AFixedAmountInMoradia_IsAFixedBillThatDoesNotVary()
    {
        var stream = Assert.Single(Detect(Monthly("Condominio Exemplo", 650m, new DateOnly(2026, 7, 10), 4, "MORADIA")));

        Assert.Equal(RecurringKinds.FixedBill, stream.Facts.Kind);
        Assert.False(stream.Facts.VariableAmount);
    }

    [Theory]
    [InlineData("LAZER", 200, RecurringKinds.Subscription)]
    [InlineData("COMPRAS", 59.90, RecurringKinds.Subscription)]
    [InlineData("OUTROS", 200.01, RecurringKinds.FixedBill)]
    [InlineData("SAUDE", 99, RecurringKinds.FixedBill)]
    [InlineData("TRANSPORTE", 99, RecurringKinds.FixedBill)]
    public void TheKind_ComesFromTheCategoryAndTheAmount(string category, double amount, string expected)
        => Assert.Equal(expected, Assert.Single(Detect(Monthly("Servico Exemplo", (decimal)amount, new DateOnly(2026, 8, 5), 3, category))).Facts.Kind);

    [Fact]
    public void ASubscriptionHint_IsASubscription_WhateverTheCategoryAndTheAmount()
        => Assert.Equal(
            RecurringKinds.Subscription,
            Assert.Single(Detect(Monthly("ADOBE *CREATIVE", 290m, new DateOnly(2026, 8, 5), 3, "MORADIA"))).Facts.Kind);

    // ---------------------------------------------------------------- price change

    [Fact]
    public void ThreeChargesOf3990_ThenTwoOf4490_AreTheSameItem_WithAPriceIncrease()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 6, 5), 3).Concat(Monthly("Streaming Exemplo", 44.90m, new DateOnly(2026, 9, 5), 2));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(5, stream.Facts.Occurrences);
        Assert.Equal(39.90m, stream.Facts.PreviousAmount);
        Assert.Equal(44.90m, stream.Facts.MedianAmount);
        Assert.Equal(44.90m, stream.Facts.LastAmount);
        Assert.Equal(44.90m * 12, stream.Facts.AnnualCost);
        Assert.Contains(RecurringFlags.PriceIncrease, stream.Facts.Flags);
    }

    /// <summary>A step above the 15% band (the old and the new price are not "similar amounts") is still the same item.</summary>
    [Fact]
    public void AStepAboveTheBand_FollowedByAStablePrice_IsTheSameItem()
    {
        var rows = Monthly("Streaming Exemplo", 30m, new DateOnly(2026, 6, 5), 3).Concat(Monthly("Streaming Exemplo", 40m, new DateOnly(2026, 9, 5), 2));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(5, stream.Facts.Occurrences);
        Assert.Equal(30m, stream.Facts.PreviousAmount);
        Assert.Equal(40m, stream.Facts.MedianAmount);
        Assert.Contains(RecurringFlags.PriceIncrease, stream.Facts.Flags);
    }

    [Fact]
    public void ASingleDifferentCharge_IsNotANewPriceYet()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 6, 5), 4).Append(Row("Streaming Exemplo", 44.90m, new DateOnly(2026, 10, 5)));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(5, stream.Facts.Occurrences);
        Assert.Null(stream.Facts.PreviousAmount);
        Assert.DoesNotContain(RecurringFlags.PriceIncrease, stream.Facts.Flags);
        Assert.Equal(39.90m, stream.Facts.MedianAmount);
        Assert.Equal(44.90m, stream.Facts.LastAmount);
    }

    [Fact]
    public void APriceThatWentDown_IsTheSameItem_WithoutTheMark()
    {
        var rows = Monthly("Streaming Exemplo", 44.90m, new DateOnly(2026, 6, 5), 3).Concat(Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 9, 5), 2));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(39.90m, stream.Facts.MedianAmount);
        Assert.Null(stream.Facts.PreviousAmount);
        Assert.DoesNotContain(RecurringFlags.PriceIncrease, stream.Facts.Flags);
    }

    // ---------------------------------------------------------------- maybe forgotten

    [Fact]
    public void SevenMonthsOfTheSameAmount_InTheSubscriptionHints_IsMaybeForgotten()
    {
        var stream = Assert.Single(Detect(Monthly("NETFLIX.COM", 129.90m, new DateOnly(2026, 4, 5), 7)));

        Assert.Equal(RecurringKinds.Subscription, stream.Facts.Kind);
        Assert.Contains(RecurringFlags.Forgotten, stream.Facts.Flags);
    }

    [Fact]
    public void TheSeriesOfTheIssue_SevenMonthsOf2990_IsMaybeForgotten()
        => Assert.Contains(RecurringFlags.Forgotten, Assert.Single(Detect(Monthly("NETFLIX.COM", 29.90m, new DateOnly(2026, 4, 5), 7))).Facts.Flags);

    [Fact]
    public void TheSameSeries_WithAPriceChangeInTheMiddle_IsNotForgotten()
    {
        var rows = Monthly("NETFLIX.COM", 29.90m, new DateOnly(2026, 4, 5), 4).Concat(Monthly("NETFLIX.COM", 34.90m, new DateOnly(2026, 8, 5), 3));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(7, stream.Facts.Occurrences);
        Assert.DoesNotContain(RecurringFlags.Forgotten, stream.Facts.Flags);
        Assert.Contains(RecurringFlags.PriceIncrease, stream.Facts.Flags);
    }

    [Theory]
    [InlineData(60, true)]      // cheap, not in the hints: forgotten
    [InlineData(60.01, false)]  // not cheap and not in the hints
    public void OutOfTheHints_OnlyACheapSubscription_IsMaybeForgotten(double amount, bool forgotten)
    {
        var stream = Assert.Single(Detect(Monthly("Clube Exemplo", (decimal)amount, new DateOnly(2026, 4, 5), 7)));

        Assert.Equal(forgotten, stream.Facts.Flags.Contains(RecurringFlags.Forgotten));
    }

    [Fact]
    public void FiveMonths_IsNotLongEnoughToBeForgotten()
        => Assert.DoesNotContain(RecurringFlags.Forgotten, Assert.Single(Detect(Monthly("NETFLIX.COM", 29.90m, new DateOnly(2026, 6, 5), 5))).Facts.Flags);

    [Fact]
    public void AFixedBill_IsNeverForgotten()
        => Assert.DoesNotContain(RecurringFlags.Forgotten, Assert.Single(Detect(Monthly("Condominio Exemplo", 50m, new DateOnly(2026, 4, 5), 7, "MORADIA"))).Facts.Flags);

    // ---------------------------------------------------------------- life cycle

    [Theory]
    [InlineData(44, RecurringStatuses.Active)]
    [InlineData(45, RecurringStatuses.SuspectedDormant)]   // 1.5 x 30 days
    [InlineData(59, RecurringStatuses.SuspectedDormant)]
    [InlineData(60, RecurringStatuses.Stopped)]            // 2 x 30 days
    public void TheStatus_ComesFromHowLongAgoTheLastChargeWas(int daysSinceLast, string expected)
    {
        var rows = Series("Streaming Exemplo", 39.90m, Today.AddDays(-daysSinceLast - 90), 4, 30);

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(Today.AddDays(-daysSinceLast), stream.Facts.LastSeenLocal);
        Assert.Equal(expected, stream.Facts.Status);
    }

    [Fact]
    public void ANewCharge_BringsAStoppedSeriesBack()
    {
        var rows = Series("Streaming Exemplo", 39.90m, Today.AddDays(-150), 4, 30);   // the last one 60 days ago: stopped
        Assert.Equal(RecurringStatuses.Stopped, Assert.Single(Detect(rows)).Facts.Status);

        rows.Add(Row("Streaming Exemplo", 39.90m, Today));

        var stream = Assert.Single(Detect(rows));
        Assert.Equal(RecurringStatuses.Active, stream.Facts.Status);
        Assert.Equal(5, stream.Facts.Occurrences);
        Assert.Equal(1, stream.Facts.MissedCount);
    }

    [Fact]
    public void AStreamWhoseFirstChargeIsRecent_IsNew()
    {
        var recent = Assert.Single(Detect(Series("Feira Exemplo", 80m, Today.AddDays(-28), 5, 7, "ALIMENTACAO")));
        Assert.Contains(RecurringFlags.New, recent.Facts.Flags);

        var old = Assert.Single(Detect(Series("Feira Exemplo", 80m, Today.AddDays(-42), 7, 7, "ALIMENTACAO")));
        Assert.DoesNotContain(RecurringFlags.New, old.Facts.Flags);
    }

    // ---------------------------------------------------------------- instalments (3.4)

    [Fact]
    public void TwoConsecutivePartsInConsecutiveMonths_AreAConfirmedInstalment()
    {
        var rows = new[] { Row("LOJA 02/10", 150m, new DateOnly(2026, 9, 12), "COMPRAS"), Row("LOJA 03/10", 150m, new DateOnly(2026, 10, 5), "COMPRAS") };

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringKinds.Installment, stream.Facts.Kind);
        Assert.Equal(RecurringConfidences.High, stream.Facts.Confidence);
        Assert.Equal(3, stream.Facts.InstallmentNumber);
        Assert.Equal(10, stream.Facts.InstallmentTotal);
        Assert.Equal(150m * 7, stream.Facts.RemainingAmount);
        Assert.Equal("2027-05", stream.Facts.EndMonth);
        Assert.Equal("LOJA", stream.Facts.DisplayName);
        Assert.Equal(150m, stream.Facts.MedianAmount);
        Assert.Equal(150m * 7, stream.Facts.AnnualCost);
        Assert.Equal(new DateOnly(2026, 11, 5), stream.Facts.NextExpectedLocal);
        Assert.StartsWith("loja#10x", stream.MerchantKey);
    }

    [Fact]
    public void TheMarkIsAlsoReadInTheDescription()
    {
        var rows = new[]
        {
            Row("Loja Exemplo", 99m, new DateOnly(2026, 9, 12), "COMPRAS", "Compra parcelada 1/3"),
            Row("Loja Exemplo", 99m, new DateOnly(2026, 10, 5), "COMPRAS", "Compra parcelada 2/3"),
        };

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringKinds.Installment, stream.Facts.Kind);
        Assert.Equal(2, stream.Facts.InstallmentNumber);
        Assert.Equal(3, stream.Facts.InstallmentTotal);
        Assert.Equal(99m, stream.Facts.RemainingAmount);
        Assert.Equal("2026-11", stream.Facts.EndMonth);
    }

    /// <summary>"15/10" cannot be part 15 of 10: alone it is a date, and it never becomes a confirmed instalment.</summary>
    [Fact]
    public void AnIsolated1510_IsNeverAConfirmedInstalment()
    {
        var streams = Detect([Row("LOJA 15/10", 150m, new DateOnly(2026, 10, 5), "COMPRAS")]);

        Assert.DoesNotContain(streams, s => s.Facts.Kind == RecurringKinds.Installment && s.Facts.Confidence != RecurringConfidences.Low);
        Assert.Empty(streams);
    }

    /// <summary>A part number above the number of parts is never an instalment, alone or in a row, whatever the month.</summary>
    [Fact]
    public void APartNumberAboveTheTotal_IsNeverAnInstalment()
    {
        Assert.Empty(Detect([Row("LOJA 15/12", 150m, new DateOnly(2026, 10, 5), "COMPRAS")]));

        var inARow = new[] { Row("LOJA 14/10", 150m, new DateOnly(2026, 9, 5), "COMPRAS"), Row("LOJA 15/10", 150m, new DateOnly(2026, 10, 5), "COMPRAS") };
        Assert.DoesNotContain(Detect(inARow), s => s.Facts.Kind == RecurringKinds.Installment);
    }

    [Fact]
    public void OneFreshMarkAlone_IsOnlyAProbableInstalment()
    {
        var stream = Assert.Single(Detect([Row("LOJA 02/06", 150m, new DateOnly(2026, 10, 5), "COMPRAS")]));

        Assert.Equal(RecurringKinds.Installment, stream.Facts.Kind);
        Assert.Equal(RecurringConfidences.Low, stream.Facts.Confidence);
        Assert.Equal(2, stream.Facts.InstallmentNumber);
        Assert.Equal(6, stream.Facts.InstallmentTotal);
    }

    [Theory]
    [InlineData("LOJA 05/10", 10, 5)]          // looks like the day and the month of the purchase
    [InlineData("LOJA 05/10/2026", 10, 5)]     // a date
    [InlineData("LOJA 02/06", 8, 20)]          // old, and the next part never came
    [InlineData("LOJA 49/50", 10, 5)]          // more than 48 parts
    [InlineData("LOJA 0/10", 10, 5)]
    [InlineData("LOJA 1/1", 10, 5)]
    public void AMarkThatIsProbablyNotAnInstalment_IsNotShown(string merchant, int month, int day)
        => Assert.Empty(Detect([Row(merchant, 150m, new DateOnly(2026, month, day), "COMPRAS")]));

    [Fact]
    public void TwoMarksThatAreNotConsecutiveParts_AreNotConfirmed()
    {
        var rows = new[] { Row("LOJA 02/10", 150m, new DateOnly(2026, 8, 12), "COMPRAS"), Row("LOJA 04/10", 150m, new DateOnly(2026, 10, 5), "COMPRAS") };

        Assert.DoesNotContain(Detect(rows), s => s.Facts.Confidence == RecurringConfidences.High);
    }

    [Fact]
    public void AnInstalmentThatWasPaidOff_IsNotListed()
    {
        var rows = new[] { Row("LOJA 02/03", 150m, new DateOnly(2026, 7, 12), "COMPRAS"), Row("LOJA 03/03", 150m, new DateOnly(2026, 8, 12), "COMPRAS") };

        Assert.Empty(Detect(rows));
    }

    [Fact]
    public void CommittedByMonth_SumsThePartsStillToBePaid_ForTheNextTwelveMonths()
    {
        var committed = RecurrenceDetector.CommittedByMonth(
            [
                (new DateOnly(2026, 10, 5), 150m, 3, 10),    // 7 parts left: November 2026 to May 2027
                (new DateOnly(2026, 9, 20), 80m, 1, 3),      // 2 parts left: October and November 2026
                (new DateOnly(2026, 10, 2), 10m, 1, 24),     // 23 parts left: only the next 12 months count
            ],
            Today);

        Assert.Equal(12, committed.Count);
        Assert.Equal(("2026-10", 80m), committed[0]);
        Assert.Equal(("2026-11", 240m), committed[1]);
        Assert.Equal(("2026-12", 160m), committed[2]);
        Assert.Equal(("2027-05", 160m), committed[7]);
        Assert.Equal(("2027-06", 10m), committed[8]);
        Assert.Equal(("2027-09", 10m), committed[11]);
    }

    // ---------------------------------------------------------------- small frequent spend (3.2)

    [Fact]
    public void FourteenSmallPurchasesInThirtyDays_AreAHabit_WithTheProjectedCostOfTwelveMonths()
    {
        var amounts = new[] { 9m, 10m, 11m, 12m, 12m, 12.50m, 12.50m, 12.50m, 12.50m, 13m, 14m, 15m, 16m, 18m };
        var rows = amounts.Select((a, i) => Row("Padaria Exemplo", a, Today.AddDays(-2 * i), "ALIMENTACAO")).ToList();

        var stream = Assert.Single(Detect(rows));

        Assert.Equal(RecurringKinds.Habit, stream.Facts.Kind);
        Assert.Equal(RecurringCadences.Irregular, stream.Cadence);
        Assert.Equal(12.50m, stream.Facts.MedianAmount);
        Assert.Equal(14, stream.Facts.Occurrences);
        Assert.Equal(amounts.Sum() * 12, stream.Facts.AnnualCost);
        Assert.Equal(RecurringStatuses.Active, stream.Facts.Status);
    }

    [Fact]
    public void ThreeSmallPurchases_OrPurchasesThatAreNotSmall_AreNotAHabit()
    {
        Assert.Empty(Detect(Enumerable.Range(0, 3).Select(i => Row("Padaria Exemplo", 12m, Today.AddDays(-3 * i), "ALIMENTACAO"))));
        Assert.Empty(Detect(new[] { 0, 3, 10, 12, 20 }.Select(i => Row("Mercado Exemplo", 50.01m + i, Today.AddDays(-i), "ALIMENTACAO"))));
        // Four purchases, but only three of them in the last 30 days.
        Assert.Empty(Detect(new[] { 0, 3, 12, 30 }.Select(i => Row("Padaria Exemplo", 12m, Today.AddDays(-i), "ALIMENTACAO"))));
    }

    // ---------------------------------------------------------------- what is never a stream

    [Theory]
    [InlineData("Pix enviado João", null)]
    [InlineData("JOAO", "TED")]
    [InlineData("MARIA S SILVA", null)]
    public void ATransferToAPerson_NeverFormsAStream(string merchant, string? description)
    {
        var monthly = Enumerable.Range(0, 6).Select(i => Row(merchant, 500m, new DateOnly(2026, 5, 5).AddMonths(i), "OUTROS", description));
        var frequent = Enumerable.Range(0, 8).Select(i => Row(merchant, 20m, Today.AddDays(-i), "OUTROS", description));

        var result = RecurrenceDetector.Detect(monthly.Concat(frequent).ToList(), Today, Options);

        Assert.Empty(result.Streams);
        Assert.DoesNotContain(MerchantKey.PersonTransfer, result.ChargesByKey.Keys);
        Assert.Empty(result.ChargesByKey);
    }

    [Fact]
    public void TheSameShopWithoutTheNameOfAPerson_DoesFormAStream()
        => Assert.Single(Detect(Monthly("Padaria Silva", 45m, new DateOnly(2026, 8, 5), 3, "OUTROS")));

    /// <summary>Statement lines (PDF import) have no merchant: the description is what the app shows as the establishment.</summary>
    [Fact]
    public void WithoutAMerchant_TheDescriptionIsTheEstablishment()
    {
        var rows = Enumerable.Range(0, 3).Select(i => Row(null, 39.90m, new DateOnly(2026, 8, 5).AddMonths(i), "LAZER", "STREAMING EXEMPLO 0800"));

        var stream = Assert.Single(Detect(rows));

        Assert.Equal("streaming exemplo", stream.MerchantKey);
        Assert.Equal("STREAMING EXEMPLO 0800", stream.Facts.DisplayName);
    }

    [Fact]
    public void ChargesOfTwoPeople_HaveNoPerson_AndAFutureChargeIsIgnored()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 8, 5), 3);
        rows[1] = rows[1] with { UserId = Bruno };
        rows.Add(Row("Streaming Exemplo", 39.90m, new DateOnly(2026, 11, 5)));

        var stream = Assert.Single(Detect(rows));

        Assert.Null(stream.Facts.UserId);
        Assert.Equal(3, stream.Facts.Occurrences);
    }

    [Fact]
    public void TheSameRows_AlwaysGiveTheSameAnswer_WhateverTheOrder()
    {
        var rows = Monthly("Streaming Exemplo", 39.90m, new DateOnly(2026, 6, 5), 5)
            .Concat(Monthly("NETFLIX.COM", 29.90m, new DateOnly(2026, 4, 5), 7))
            .Concat(Series("Feira Exemplo", 80m, new DateOnly(2026, 9, 15), 4, 7, "ALIMENTACAO"))
            .ToList();

        var forward = Detect(rows).Select(s => (s.MerchantKey, s.Cadence, s.Facts.Kind, s.Facts.Occurrences)).ToList();
        var backward = Detect(Enumerable.Reverse(rows)).Select(s => (s.MerchantKey, s.Cadence, s.Facts.Kind, s.Facts.Occurrences)).ToList();

        Assert.Equal(3, forward.Count);
        Assert.Equal(forward, backward);
    }
}
