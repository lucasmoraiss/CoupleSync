using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.UnitTests.Domain;

public sealed class CurrencyRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BRL")]
    [InlineData("brl")]
    [InlineData(" Brl ")]
    public void AbsentOrBrl_IsAcceptedAndStoredAsBrl(string? input)
    {
        Assert.True(CurrencyRules.IsAccepted(input));
        Assert.Equal("BRL", CurrencyRules.TryNormalize(input));
        Assert.Equal("BRL", CurrencyRules.NormalizeOrBrl(input));
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("usd")]
    [InlineData("R$")]
    [InlineData("REAIS")]
    [InlineData("BR")]
    public void AnyOtherCurrency_IsRejected(string input)
    {
        Assert.False(CurrencyRules.IsAccepted(input));
        Assert.Null(CurrencyRules.TryNormalize(input));
    }

    [Theory]
    [InlineData("BRL", true)]
    [InlineData("brl", true)]
    [InlineData(" BRL ", true)]
    [InlineData("USD", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBrl_DecidesWhatEntersSumsInReais(string? stored, bool expected)
        => Assert.Equal(expected, CurrencyRules.IsBrl(stored));
}
