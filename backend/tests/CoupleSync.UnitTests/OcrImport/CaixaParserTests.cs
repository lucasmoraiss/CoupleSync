using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "CaixaParser")]
public sealed class CaixaParserTests
{
    private const string ValidHeader = "CAIXA ECONÔMICA FEDERAL - caixa.gov.br\nExtrato Conta Poupança\n";

    private const string FixtureText = ValidHeader +
        "08/07/2024  Saque Caixa Eletrônico  300,00  D\n" +
        "10/07/2024  Crédito FGTS  1.200,00  C\n" +
        "11/07/2024  Pagamento Conta Água  85,40  D\n";

    private readonly CaixaParser _sut = new();

    [Fact]
    public void CanParse_ReturnsTrue_ForCaixaText()
    {
        Assert.True(_sut.CanParse(FixtureText));
    }

    [Fact]
    public void CanParse_ReturnsTrue_ForCEFAbbreviation()
    {
        Assert.True(_sut.CanParse("CEF - Caixa"));
    }

    [Fact]
    public void CanParse_ReturnsFalse_ForNonCaixaText()
    {
        Assert.False(_sut.CanParse("Banco Mercantil do Brasil extrato"));
    }

    [Fact]
    public void Parse_ReturnsCorrectTransactionList_FromFixtureText()
    {
        var result = _sut.Parse(FixtureText);

        Assert.Equal(3, result.Count);

        Assert.Equal(new DateTime(2024, 7, 8), result[0].Date);
        Assert.Equal("Saque Caixa Eletrônico", result[0].Description);
        Assert.Equal(300.00m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);

        Assert.Equal(new DateTime(2024, 7, 10), result[1].Date);
        Assert.Equal("Crédito FGTS", result[1].Description);
        Assert.Equal(1200.00m, result[1].Amount);
        Assert.Equal(TransactionType.Credit, result[1].Type);

        Assert.Equal(new DateTime(2024, 7, 11), result[2].Date);
        Assert.Equal("Pagamento Conta Água", result[2].Description);
        Assert.Equal(85.40m, result[2].Amount);
        Assert.Equal(TransactionType.Debit, result[2].Type);
    }

    [Fact]
    public void Parse_ReturnsEmpty_WhenNoTransactionLines()
    {
        var result = _sut.Parse(ValidHeader + "Sem movimentações.\n");
        Assert.Empty(result);
    }

    [Fact]
    public void BankName_IsCaixa()
    {
        Assert.Equal("Caixa", _sut.BankName);
    }
}
