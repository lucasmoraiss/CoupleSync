using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "InterParser")]
public sealed class InterBankParserTests
{
    private const string ValidHeader = "Banco Inter S.A.\nExtrato de conta corrente\n";

    private const string FixtureText = ValidHeader +
        "10/03/2024  Pagamento Boleto  R$ 250,00  D\n" +
        "12/03/2024  Transferência Recebida  R$ 1.000,00  C\n" +
        "15/03/2024  Mercado Pague Menos  R$ 89,50  D\n";

    private readonly InterBankParser _sut = new();

    [Fact]
    public void CanParse_ReturnsTrue_ForInterText()
    {
        Assert.True(_sut.CanParse(FixtureText));
    }

    [Fact]
    public void CanParse_ReturnsFalse_ForNonInterText()
    {
        Assert.False(_sut.CanParse("NUBANK S.A. extrato"));
    }

    [Fact]
    public void Parse_ReturnsCorrectTransactionList_FromFixtureText()
    {
        var result = _sut.Parse(FixtureText);

        Assert.Equal(3, result.Count);

        Assert.Equal(new DateTime(2024, 3, 10), result[0].Date);
        Assert.Equal("Pagamento Boleto", result[0].Description);
        Assert.Equal(250.00m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);

        Assert.Equal(new DateTime(2024, 3, 12), result[1].Date);
        Assert.Equal("Transferência Recebida", result[1].Description);
        Assert.Equal(1000.00m, result[1].Amount);
        Assert.Equal(TransactionType.Credit, result[1].Type);

        Assert.Equal(new DateTime(2024, 3, 15), result[2].Date);
        Assert.Equal("Mercado Pague Menos", result[2].Description);
        Assert.Equal(89.50m, result[2].Amount);
        Assert.Equal(TransactionType.Debit, result[2].Type);
    }

    [Fact]
    public void Parse_ReturnsEmpty_WhenNoTransactionLines()
    {
        var result = _sut.Parse(ValidHeader + "Sem movimentações.\n");
        Assert.Empty(result);
    }

    [Fact]
    public void BankName_IsInter()
    {
        Assert.Equal("Inter", _sut.BankName);
    }
}
