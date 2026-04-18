using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "NubankParser")]
public sealed class NubankParserTests
{
    private const string ValidHeader = "Nu Pagamentos S.A. - nubank.com.br\nExtrato de conta\n";

    private const string FixtureText = ValidHeader +
        "03/04/2024  Supermercado Extra  -R$ 150,00\n" +
        "05/04/2024  Salário Empresa ABC  +R$ 3.500,00\n" +
        "07/04/2024  Netflix Assinatura  -R$ 45,90\n";

    private readonly NubankParser _sut = new();

    [Fact]
    public void CanParse_ReturnsTrue_ForNubankText()
    {
        Assert.True(_sut.CanParse(FixtureText));
    }

    [Fact]
    public void CanParse_ReturnsFalse_ForNonNubankText()
    {
        Assert.False(_sut.CanParse("Banco do Brasil S.A.\nExtrato"));
    }

    [Fact]
    public void CanParse_ReturnsTrue_ForNubankDotComBr()
    {
        Assert.True(_sut.CanParse("Acesse nubank.com.br para mais detalhes"));
    }

    [Fact]
    public void Parse_ReturnsCorrectTransactionList_FromFixtureText()
    {
        var result = _sut.Parse(FixtureText);

        Assert.Equal(3, result.Count);

        Assert.Equal(new DateTime(2024, 4, 3), result[0].Date);
        Assert.Equal("Supermercado Extra", result[0].Description);
        Assert.Equal(150.00m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);

        Assert.Equal(new DateTime(2024, 4, 5), result[1].Date);
        Assert.Equal("Salário Empresa ABC", result[1].Description);
        Assert.Equal(3500.00m, result[1].Amount);
        Assert.Equal(TransactionType.Credit, result[1].Type);

        Assert.Equal(new DateTime(2024, 4, 7), result[2].Date);
        Assert.Equal("Netflix Assinatura", result[2].Description);
        Assert.Equal(45.90m, result[2].Amount);
        Assert.Equal(TransactionType.Debit, result[2].Type);
    }

    [Fact]
    public void Parse_ReturnsEmpty_WhenNoTransactionLines()
    {
        var result = _sut.Parse(ValidHeader + "Sem movimentações no período.\n");
        Assert.Empty(result);
    }

    [Fact]
    public void BankName_IsNubank()
    {
        Assert.Equal("Nubank", _sut.BankName);
    }
}
