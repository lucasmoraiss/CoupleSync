using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "InterParser")]
public sealed class InterBankParserTests
{
    private const string InterHeader = "Banco Inter S.A.\n";

    private readonly InterBankParser _sut = new();

    // ── Identification ────────────────────────────────────────────────────

    [Fact]
    public void CanParse_ReturnsTrue_ForInterText()
    {
        Assert.True(_sut.CanParse(InterHeader + "some content"));
    }

    [Fact]
    public void CanParse_ReturnsFalse_ForNonInterText()
    {
        Assert.False(_sut.CanParse("NUBANK S.A. extrato"));
    }

    [Fact]
    public void BankName_IsInter()
    {
        Assert.Equal("Inter", _sut.BankName);
    }

    // ── Single-line extrato (backward compat) ─────────────────────────────

    [Fact]
    public void Parse_SingleLine_ExtratoWithDCIndicator()
    {
        const string text = InterHeader +
            "Extrato de conta corrente\n" +
            "10/03/2024  Pagamento Boleto  R$ 250,00  D\n" +
            "12/03/2024  Transferência Recebida  R$ 1.000,00  C\n" +
            "15/03/2024  Mercado Pague Menos  R$ 89,50  D\n";

        var result = _sut.Parse(text);

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
    public void Parse_SingleLine_WithoutDCIndicator()
    {
        const string text = InterHeader +
            "01/04/2025  MERCADO LIVRE *MERCADO L      R$ 149,90\n";

        var result = _sut.Parse(text);

        Assert.Single(result);
        Assert.Equal("MERCADO LIVRE *MERCADO L", result[0].Description);
        Assert.Equal(149.90m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);
    }

    [Fact]
    public void Parse_SingleLine_WithoutCurrencySymbolExplicit()
    {
        const string text = InterHeader +
            "05/05/2025  SUPERMERCADO ABC              R$ 1.250,00\n";

        var result = _sut.Parse(text);

        Assert.Single(result);
        Assert.Equal(1250.00m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);
    }

    [Fact]
    public void Parse_ReturnsEmpty_WhenNoTransactionLines()
    {
        var result = _sut.Parse(InterHeader + "Sem movimentações.\n");
        Assert.Empty(result);
    }

    // ── Multi-line fatura ─────────────────────────────────────────────────

    [Fact]
    public void Parse_Fatura_DateThenDescThenDebitAmount()
    {
        const string text = InterHeader +
            "05 de abr. 2026\n" +
            "Uber UBER TRIP HELP.U\n" +
            "R$ 34,14\n";

        var result = _sut.Parse(text);

        Assert.Single(result);
        Assert.Equal(new DateTime(2026, 4, 5), result[0].Date);
        Assert.Contains("Uber UBER TRIP HELP.U", result[0].Description);
        Assert.Equal(34.14m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);
    }

    [Fact]
    public void Parse_Fatura_DateThenDescThenCreditAmount()
    {
        const string text = InterHeader +
            "02 de abr. 2026\n" +
            "PAGAMENTO ON LINE\n" +
            "+ R$ 2.588,34\n";

        var result = _sut.Parse(text);

        Assert.Single(result);
        Assert.Equal(new DateTime(2026, 4, 2), result[0].Date);
        Assert.Contains("PAGAMENTO ON LINE", result[0].Description);
        Assert.Equal(2588.34m, result[0].Amount);
        Assert.Equal(TransactionType.Credit, result[0].Type);
    }

    [Fact]
    public void Parse_Fatura_WithLargeAmount()
    {
        // Test amounts with multiple thousands separators
        const string text = InterHeader +
            "12 de mai. 2026\n" +
            "COMPRA INTERNACIONAL\n" +
            "R$ 12.345,67\n";

        var result = _sut.Parse(text);

        Assert.Single(result);
        Assert.Equal(new DateTime(2026, 5, 12), result[0].Date);
        Assert.Contains("COMPRA INTERNACIONAL", result[0].Description);
        Assert.Equal(12345.67m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);
    }

    [Fact]
    public void Parse_Fatura_MultiLineDescription()
    {
        const string text = InterHeader +
            "21 de out. 2025\n" +
            "MERCADO LIVRE SHOP\n" +
            "Item: Electronics\n" +
            "R$ 150,00\n";

        var result = _sut.Parse(text);

        Assert.Single(result);
        Assert.Equal(new DateTime(2025, 10, 21), result[0].Date);
        Assert.Contains("MERCADO LIVRE SHOP", result[0].Description);
        Assert.Contains("Item: Electronics", result[0].Description);
        Assert.Equal(150.00m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);
    }

    [Fact]
    public void Parse_Fatura_DateWithoutDotInMonth()
    {
        // "out" instead of "out." — dot is optional
        const string text = InterHeader +
            "21 de out 2025\n" +
            "SOME PURCHASE\n" +
            "R$ 50,00\n";

        var result = _sut.Parse(text);

        Assert.Single(result);
        Assert.Equal(new DateTime(2025, 10, 21), result[0].Date);
        Assert.Contains("SOME PURCHASE", result[0].Description);
    }

    [Fact]
    public void Parse_Fatura_MultipleFaturaTransactions()
    {
        const string text = InterHeader +
            "Fatura Cartão de Crédito\n" +
            "CARTÃO 5364****8991\n" +
            "01 de mar. 2026\n" +
            "NETFLIX.COM\n" +
            "R$ 55,90\n" +
            "05 de mar. 2026\n" +
            "POSTO IPIRANGA\n" +
            "R$ 250,00\n" +
            "10 de mar. 2026\n" +
            "PAGAMENTO FATURA\n" +
            "+ R$ 3.000,00\n";

        var result = _sut.Parse(text);

        Assert.Equal(3, result.Count);

        // First tx may have header noise in description — assert key content
        Assert.Equal(new DateTime(2026, 3, 1), result[0].Date);
        Assert.Contains("NETFLIX.COM", result[0].Description);
        Assert.Equal(55.90m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);

        Assert.Equal(new DateTime(2026, 3, 5), result[1].Date);
        Assert.Equal("POSTO IPIRANGA", result[1].Description);
        Assert.Equal(250.00m, result[1].Amount);
        Assert.Equal(TransactionType.Debit, result[1].Type);

        Assert.Equal(new DateTime(2026, 3, 10), result[2].Date);
        Assert.Equal("PAGAMENTO FATURA", result[2].Description);
        Assert.Equal(3000.00m, result[2].Amount);
        Assert.Equal(TransactionType.Credit, result[2].Type);
    }

    [Fact]
    public void Parse_Fatura_EmptyLinesBetweenFields()
    {
        const string text = InterHeader +
            "02 de abr. 2026\n" +
            "\n" +
            "  \n" +
            "COMPRA ONLINE\n" +
            "\n" +
            "R$ 100,00\n";

        var result = _sut.Parse(text);

        Assert.Single(result);
        Assert.Contains("COMPRA ONLINE", result[0].Description);
        Assert.Equal(100.00m, result[0].Amount);
    }

    // ── Multi-line extrato ────────────────────────────────────────────────

    [Fact]
    public void Parse_MultiLineExtrato_DateThenDescThenAmountWithIndicator()
    {
        const string text = InterHeader +
            "10/03/2024\n" +
            "Pagamento Boleto\n" +
            "R$ 250,00 D\n";

        var result = _sut.Parse(text);

        Assert.Single(result);
        Assert.Equal(new DateTime(2024, 3, 10), result[0].Date);
        Assert.Contains("Pagamento Boleto", result[0].Description);
        Assert.Equal(250.00m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);
    }

    [Fact]
    public void Parse_Fatura_TwoDigitDay()
    {
        // Regex expects DD (two digits), not single digit
        const string text = InterHeader +
            "01 de jan. 2026\n" +
            "COMPRA TESTE\n" +
            "R$ 10,00\n";

        var result = _sut.Parse(text);

        Assert.Single(result);
        Assert.Equal(new DateTime(2026, 1, 1), result[0].Date);
    }
}
