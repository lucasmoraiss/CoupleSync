using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.UnitTests.Time;

/// <summary>
/// BrazilTime uses the real America/Sao_Paulo zone when the host has it (tzdata/Windows) and a fixed UTC-3
/// otherwise. Both must answer every question, and the same way for everything since daylight saving ended.
/// </summary>
[Trait("Category", "BrazilMonth")]
public sealed class BrazilTimeTotalityTests
{
    [Theory]
    // 2018-11-04 00:00 did not exist in Brasília (clocks jumped from 00:00 to 01:00).
    [InlineData("2018-11-04T00:00:00", "2018-11-04T03:00:00Z")]
    [InlineData("2018-11-04T00:30:00", "2018-11-04T03:30:00Z")]
    [InlineData("2017-10-15T00:00:00", "2017-10-15T03:00:00Z")]
    public void ToUtc_OfAWallClockTimeSkippedByDaylightSaving_DoesNotThrow(string local, string expectedUtc)
    {
        var utc = BrazilTime.ToUtc(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(DateTime.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void ToUtc_OfEveryMidnightSince1985_Works_AndDaysNeverGoBackwards()
    {
        var previous = DateTime.MinValue;
        for (var day = new DateTime(1985, 1, 1); day < new DateTime(2035, 1, 1); day = day.AddDays(1))
        {
            var utc = BrazilTime.ToUtc(day);
            Assert.True(utc > previous, $"{day:yyyy-MM-dd}: {utc:O} is not after {previous:O}");
            previous = utc;
        }
    }

    [Fact]
    public void SinceDaylightSavingEnded_TheRealZoneAndTheFixedOffsetGiveTheSameAnswers()
    {
        // Daylight saving ended for good on 2019-02-17 (02:00 UTC). From then on Brasília is UTC-3, which is what
        // the fallback for hosts without a zone database assumes. If this fails, the zone data changed (Brazil
        // brought daylight saving back) and the fixed fallback in BrazilTime is no longer exact.
        for (var utc = new DateTime(2019, 2, 17, 2, 0, 0, DateTimeKind.Utc); utc < new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc); utc = utc.AddHours(1))
        {
            var local = BrazilTime.ToLocal(utc);
            Assert.Equal(utc.AddHours(-3).Ticks, local.Ticks);
            Assert.Equal(utc, BrazilTime.ToUtc(local));
        }
    }
}
