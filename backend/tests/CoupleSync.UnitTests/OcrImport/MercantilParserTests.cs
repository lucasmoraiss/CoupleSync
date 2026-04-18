using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "MercantilParser")]
public sealed class MercantilParserTests
{
    private const string ValidHeader = "Banco Mercantil do Brasil S.A. - mercantil.com.br\nExtrato Conta Corrente\n";

    private const string FixtureText = ValidHeader +
        "15/08/2024  Compra Online Loja X  450,00  D\n" +
        "17/08/2024  Transferência PIX Recebida  2.500,00  C\n" +
        "19/08/2024  Pagamento Aluguel  1.800,00  D\n";

    private readonly MercantilParser _sut = new();

    [Fact]
    public void CanParse_ReturnsTrue_ForMercantilText()
    {
        Assert.True(_sut.CanParse(FixtureText));
    }

    [Fact]
    public void CanParse_ReturnsFalse_ForNonMercantilText()
    {
        Assert.False(_sut.CanParse("Banco Inter S.A. extrato"));
    }

    [Fact]
    public void Parse_ReturnsCorrectTransactionList_FromFixtureText()
    {
        var result = _sut.Parse(FixtureText);

        Assert.Equal(3, result.Count);

        Assert.Equal(new DateTime(2024, 8, 15), result[0].Date);
        Assert.Equal("Compra Online Loja X", result[0].Description);
        Assert.Equal(450.00m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);

        Assert.Equal(new DateTime(2024, 8, 17), result[1].Date);
        Assert.Equal("Transferência PIX Recebida", result[1].Description);
        Assert.Equal(2500.00m, result[1].Amount);
        Assert.Equal(TransactionType.Credit, result[1].Type);

        Assert.Equal(new DateTime(2024, 8, 19), result[2].Date);
        Assert.Equal("Pagamento Aluguel", result[2].Description);
        Assert.Equal(1800.00m, result[2].Amount);
        Assert.Equal(TransactionType.Debit, result[2].Type);
    }

    [Fact]
    public void Parse_ReturnsEmpty_WhenNoTransactionLines()
    {
        var result = _sut.Parse(ValidHeader + "Sem movimentações no extrato.\n");
        Assert.Empty(result);
    }

    [Fact]
    public void BankName_IsMercantil()
    {
        Assert.Equal("Mercantil", _sut.BankName);
    }
}
