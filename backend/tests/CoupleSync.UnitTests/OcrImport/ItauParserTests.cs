using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "ItauParser")]
public sealed class ItauParserTests
{
    private const string ValidHeader = "Itaú Unibanco S.A.\nExtrato de conta\n";

    // ItauParser uses short date (DD/MM) with trailing minus = Debit
    private static readonly string FixtureText = ValidHeader +
        $"10/04  Débito Farmácia  99,90-\n" +
        $"12/04  Crédito PIX Recebido  2.000,00\n" +
        $"14/04  Supermercado Redes  350,75-\n";

    private readonly ItauParser _sut = new();

    [Fact]
    public void CanParse_ReturnsTrue_ForItauText()
    {
        Assert.True(_sut.CanParse(FixtureText));
    }

    [Fact]
    public void CanParse_ReturnsFalse_ForNonItauText()
    {
        Assert.False(_sut.CanParse("Banco do Brasil S.A. extrato"));
    }

    [Fact]
    public void Parse_ReturnsCorrectTransactionList_FromFixtureText()
    {
        var result = _sut.Parse(FixtureText);

        Assert.Equal(3, result.Count);

        Assert.Equal(4, result[0].Date.Month);
        Assert.Equal(10, result[0].Date.Day);
        Assert.Equal("Débito Farmácia", result[0].Description);
        Assert.Equal(99.90m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);

        Assert.Equal(4, result[1].Date.Month);
        Assert.Equal(12, result[1].Date.Day);
        Assert.Equal("Crédito PIX Recebido", result[1].Description);
        Assert.Equal(2000.00m, result[1].Amount);
        Assert.Equal(TransactionType.Credit, result[1].Type);

        Assert.Equal(4, result[2].Date.Month);
        Assert.Equal(14, result[2].Date.Day);
        Assert.Equal("Supermercado Redes", result[2].Description);
        Assert.Equal(350.75m, result[2].Amount);
        Assert.Equal(TransactionType.Debit, result[2].Type);
    }

    [Fact]
    public void Parse_ReturnsEmpty_WhenNoTransactionLines()
    {
        var result = _sut.Parse(ValidHeader + "Sem lançamentos.\n");
        Assert.Empty(result);
    }

    [Fact]
    public void BankName_IsItau()
    {
        Assert.Equal("Itaú", _sut.BankName);
    }
}
