using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "BancoBrasilParser")]
public sealed class BancoBrasilParserTests
{
    private const string ValidHeader = "Banco do Brasil S.A. - bb.com.br\nExtrato Conta Corrente\n";

    private const string FixtureText = ValidHeader +
        "02/05/2024  Compra Débito Posto  200,00  D\n" +
        "04/05/2024  Crédito Salário  5.000,00  C\n" +
        "06/05/2024  Conta de Luz  180,50  D\n";

    private readonly BancoBrasilParser _sut = new();

    [Fact]
    public void CanParse_ReturnsTrue_ForBancoBrasilText()
    {
        Assert.True(_sut.CanParse(FixtureText));
    }

    [Fact]
    public void CanParse_ReturnsFalse_ForNonBancoBrasilText()
    {
        Assert.False(_sut.CanParse("Santander S.A. extrato"));
    }

    [Fact]
    public void Parse_ReturnsCorrectTransactionList_FromFixtureText()
    {
        var result = _sut.Parse(FixtureText);

        Assert.Equal(3, result.Count);

        Assert.Equal(new DateTime(2024, 5, 2), result[0].Date);
        Assert.Equal("Compra Débito Posto", result[0].Description);
        Assert.Equal(200.00m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);

        Assert.Equal(new DateTime(2024, 5, 4), result[1].Date);
        Assert.Equal("Crédito Salário", result[1].Description);
        Assert.Equal(5000.00m, result[1].Amount);
        Assert.Equal(TransactionType.Credit, result[1].Type);

        Assert.Equal(new DateTime(2024, 5, 6), result[2].Date);
        Assert.Equal("Conta de Luz", result[2].Description);
        Assert.Equal(180.50m, result[2].Amount);
        Assert.Equal(TransactionType.Debit, result[2].Type);
    }

    [Fact]
    public void Parse_ReturnsEmpty_WhenNoTransactionLines()
    {
        var result = _sut.Parse(ValidHeader + "Período sem movimentações.\n");
        Assert.Empty(result);
    }

    [Fact]
    public void BankName_IsBancoDoBrasil()
    {
        Assert.Equal("Banco do Brasil", _sut.BankName);
    }
}
