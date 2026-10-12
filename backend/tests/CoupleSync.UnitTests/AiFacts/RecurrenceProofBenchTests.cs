using CoupleSync.Application.AiFacts;

namespace CoupleSync.UnitTests.AiFacts;

/// <summary>
/// Issue #39 — the guard of the rule of proof: made-up groups from fixed seeds (<see cref="RecurrenceProofBench"/>)
/// through the real detector. A guard against regression, not a calibration: the limits sit between what the rule
/// measures (design 3.3 has the numbers: 0,00–0,06 false items per group, 94–100% of the groups clean, 11,9–12,0
/// true ones confirmed, under 1,1 possible) and the goals it was accepted with (0,3 · 80% · 11,5 · 3), so that a
/// change in the detector that brings shopping habits back to the total, or loses what is truly recurring, does
/// not go unnoticed. Two of the sets of seeds are the ones the rule was studied and checked with; the 64,xM ones
/// were never used for either. Deterministic: the same seeds, the same "today".
/// </summary>
public sealed class RecurrenceProofBenchTests
{
    private const int Groups = 30;

    /// <summary>12 true recurrences next to 13 shopping places (about 900 purchases in 13 months; twice that in the lifelike shape).</summary>
    [Theory]
    [InlineData(39_400_000, false)]
    [InlineData(71_600_000, true)]
    [InlineData(64_400_000, false)]
    [InlineData(64_600_000, true)]
    public void InAGroup_TheTotalIsClean_TheTrueOnesAreConfirmed_AndThePossibleOnesAreFew(int seedBase, bool lifelike)
    {
        var shape = lifelike ? RecurrenceProofBench.Shape.Lifelike : RecurrenceProofBench.Shape.Uniform;

        var result = RecurrenceProofBench.MeasureGroups(seedBase, Groups, wide: false, shape, new RecurrenceOptions());

        Assert.Equal(12, result.TrueCount);
        Assert.True(result.FalseInTotalAverage <= 0.15m, $"False items in the total per group: {result.FalseInTotalAverage} ({Places(result)}).");
        Assert.True(result.CleanGroups >= Groups * 9 / 10, $"Groups with a clean total: {result.CleanGroups} of {Groups} ({Places(result)}).");
        Assert.True(result.TrueConfirmedAverage >= 11.8m, $"True ones confirmed per group: {result.TrueConfirmedAverage} of 12.");
        Assert.True(result.PossibleAverage <= 2m, $"Possible ones per group: {result.PossibleAverage}.");
    }

    /// <summary>
    /// The same group with the hard cases: 26 months of purchases, ten shops visited now and then, a service of one
    /// price on any day (the known weak spot: it is confirmed when three visits in a row fall in the same week of
    /// the month) and three more true ones — one of them a weekly plan, which is never in the total.
    /// </summary>
    [Theory]
    [InlineData(39_500_000, false)]
    [InlineData(64_700_000, true)]
    public void InAGroupWithTheHardCases_TheTotalIsStillNearlyClean(int seedBase, bool lifelike)
    {
        var shape = lifelike ? RecurrenceProofBench.Shape.Lifelike : RecurrenceProofBench.Shape.Uniform;

        var result = RecurrenceProofBench.MeasureGroups(seedBase, 20, wide: true, shape, new RecurrenceOptions());

        Assert.Equal(15, result.TrueCount);
        Assert.True(result.FalseInTotalAverage <= 0.35m, $"False items in the total per group: {result.FalseInTotalAverage} ({Places(result)}).");
        Assert.True(result.CleanGroups >= 15, $"Groups with a clean total: {result.CleanGroups} of 20 ({Places(result)}).");
        Assert.True(result.TrueConfirmedAverage >= 13.4m, $"True ones confirmed per group: {result.TrueConfirmedAverage} of 15 (14 can be).");
        Assert.True(result.PossibleAverage <= 2.5m, $"Possible ones per group: {result.PossibleAverage}.");
    }

    /// <summary>
    /// Review 3, I1 — the place of a weekly habit (fuel every Saturday) with a bigger purchase once a month: the
    /// habit is listed apart and the bigger purchases are no fixed bill. 300 histories; at most 1% with anything in
    /// the total (measured: 0 to 1 history), with the weekly rhythm or without it.
    /// </summary>
    [Theory]
    [InlineData(39_600_000, true)]
    [InlineData(39_600_000, false)]
    [InlineData(64_800_000, true)]
    public void APlaceWithAWeeklyHabit_PutsAlmostNothingInTheTotal(int seedBase, bool everySaturday)
    {
        var result = RecurrenceProofBench.MeasureWeeklyHabit(seedBase, 300, everySaturday, new RecurrenceOptions());

        Assert.True(result.WithItemInTotal <= 3, $"{result.Label}: {result.WithItemInTotal} of {result.Histories} histories with an item in the total.");
    }

    private static string Places(RecurrenceProofBench.GroupResult result)
        => result.FalseInTotalByPlace.Count == 0 ? "none" : string.Join(", ", result.FalseInTotalByPlace.Select(place => $"{place.Key} {place.Value}"));
}
