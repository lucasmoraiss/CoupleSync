using CoupleSync.Application.AiFacts;

namespace CoupleSync.UnitTests.AiFacts;

/// <summary>
/// Issue #39 — the guard of the rule of proof: made-up groups from fixed seeds (<see cref="RecurrenceProofBench"/>)
/// through the real detector. The limits are the goals the rule was accepted with, and are loose next to what it
/// measures (design 3.3 has the numbers): they are here so that a change in the detector that brings shopping
/// habits back to the total, or loses what is truly recurring, does not go unnoticed. Deterministic: the same seeds,
/// the same "today".
/// </summary>
public sealed class RecurrenceProofBenchTests
{
    private const int Groups = 30;

    /// <summary>12 true recurrences next to 13 shopping places (about 900 purchases in 13 months; twice that in the lifelike shape).</summary>
    [Theory]
    [InlineData(39_400_000, false)]
    [InlineData(71_600_000, true)]
    public void InAGroup_TheTotalIsClean_TheTrueOnesAreConfirmed_AndThePossibleOnesAreFew(int seedBase, bool lifelike)
    {
        var shape = lifelike ? RecurrenceProofBench.Shape.Lifelike : RecurrenceProofBench.Shape.Uniform;

        var result = RecurrenceProofBench.MeasureGroups(seedBase, Groups, wide: false, shape, new RecurrenceOptions());

        Assert.Equal(12, result.TrueCount);
        Assert.True(result.FalseInTotalAverage <= 0.3m, $"False items in the total per group: {result.FalseInTotalAverage} ({Places(result)}).");
        Assert.True(result.CleanGroups >= Groups * 8 / 10, $"Groups with a clean total: {result.CleanGroups} of {Groups} ({Places(result)}).");
        Assert.True(result.TrueConfirmedAverage >= 11.5m, $"True ones confirmed per group: {result.TrueConfirmedAverage} of 12.");
        Assert.True(result.PossibleAverage <= 3m, $"Possible ones per group: {result.PossibleAverage}.");
    }

    /// <summary>
    /// The same group with the hard cases: 26 months of purchases, ten shops visited now and then, a service of one
    /// price on any day (the known weak spot: it is confirmed when three visits in a row fall in the same week of
    /// the month) and three more true ones — one of them a weekly plan, which is never in the total.
    /// </summary>
    [Fact]
    public void InAGroupWithTheHardCases_TheTotalIsStillNearlyClean()
    {
        var result = RecurrenceProofBench.MeasureGroups(39_500_000, 20, wide: true, RecurrenceProofBench.Shape.Uniform, new RecurrenceOptions());

        Assert.Equal(15, result.TrueCount);
        Assert.True(result.FalseInTotalAverage <= 0.5m, $"False items in the total per group: {result.FalseInTotalAverage} ({Places(result)}).");
        Assert.True(result.CleanGroups >= 14, $"Groups with a clean total: {result.CleanGroups} of 20 ({Places(result)}).");
        Assert.True(result.TrueConfirmedAverage >= 13m, $"True ones confirmed per group: {result.TrueConfirmedAverage} of 15 (14 can be).");
        Assert.True(result.PossibleAverage <= 3m, $"Possible ones per group: {result.PossibleAverage}.");
    }

    private static string Places(RecurrenceProofBench.GroupResult result)
        => result.FalseInTotalByPlace.Count == 0 ? "none" : string.Join(", ", result.FalseInTotalByPlace.Select(place => $"{place.Key} {place.Value}"));
}
