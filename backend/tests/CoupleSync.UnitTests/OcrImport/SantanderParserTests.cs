using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "SantanderParser")]
public sealed class SantanderParserTests
{
    private const string ValidHeader = "Santander Brasil S.A. - santander.com.br\nExtrato Conta\n";

    private const string FixtureText = ValidHeader +
        "01/06/2024  Farmácia Popular  120,00  D\n" +
        "03/06/2024  Depósito em Espécie  500,00  C\n" +
        "05/06/2024  Academia Fitness  80,00  D\n";

    private readonly SantanderParser _sut = new();

    [Fact]
    public void CanParse_ReturnsTrue_ForSantanderText()
    {
        Assert.True(_sut.CanParse(FixtureText));
    }

    [Fact]
    public void CanParse_ReturnsFalse_ForNonSantanderText()
    {
        Assert.False(_sut.CanParse("Nu Pagamentos S.A. extrato"));
    }

    [Fact]
    public void Parse_ReturnsCorrectTransactionList_FromFixtureText()
    {
        var result = _sut.Parse(FixtureText);

        Assert.Equal(3, result.Count);

        Assert.Equal(new DateTime(2024, 6, 1), result[0].Date);
        Assert.Equal("Farmácia Popular", result[0].Description);
        Assert.Equal(120.00m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);

        Assert.Equal(new DateTime(2024, 6, 3), result[1].Date);
        Assert.Equal("Depósito em Espécie", result[1].Description);
        Assert.Equal(500.00m, result[1].Amount);
        Assert.Equal(TransactionType.Credit, result[1].Type);

        Assert.Equal(new DateTime(2024, 6, 5), result[2].Date);
        Assert.Equal("Academia Fitness", result[2].Description);
        Assert.Equal(80.00m, result[2].Amount);
        Assert.Equal(TransactionType.Debit, result[2].Type);
    }

    [Fact]
    public void Parse_ReturnsEmpty_WhenNoTransactionLines()
    {
        var result = _sut.Parse(ValidHeader + "Sem movimentações no período.\n");
        Assert.Empty(result);
    }

    [Fact]
    public void BankName_IsSantander()
    {
        Assert.Equal("Santander", _sut.BankName);
    }
}
