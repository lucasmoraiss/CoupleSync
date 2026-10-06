using System.Globalization;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.UnitTests.Notifications;

public sealed class BrlFormatTests
{
    [Theory]
    [InlineData(0, "R$ 0,00")]
    [InlineData(5, "R$ 5,00")]
    [InlineData(0.5, "R$ 0,50")]
    [InlineData(999.99, "R$ 999,99")]
    [InlineData(1000, "R$ 1.000,00")]
    [InlineData(1234.56, "R$ 1.234,56")]
    [InlineData(1234567.891, "R$ 1.234.567,89")]
    [InlineData(-1234.5, "-R$ 1.234,50")]
    [InlineData(0.004, "R$ 0,00")]
    public void Format_UsesDotThousandsAndCommaDecimals(double amount, string expected)
        => Assert.Equal(expected, BrlFormat.Format((decimal)amount));

    [Fact]
    public void Format_DoesNotDependOnTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            Assert.Equal("R$ 1.234,56", BrlFormat.Format(1234.56m));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
