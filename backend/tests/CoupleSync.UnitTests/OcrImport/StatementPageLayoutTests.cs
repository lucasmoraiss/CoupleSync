using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>
/// C03 (Nubank, Banco do Brasil, Itaú: several lines on one page) and C04 (Inter: installment purchases).
/// PdfPig can hand a whole page over as a single line, with the next date glued to the previous amount.
/// Every text here is synthetic (invented merchants, amounts and dates); only the layout described in
/// the test session log is reproduced. Never paste text from a real statement into this file.
/// </summary>
[Trait("Category", "StatementLayouts")]
public sealed class StatementPageLayoutTests
{
    // ── C03: Nubank ────────────────────────────────────────────────────────

    private const string NubankLines =
        "Nu Pagamentos S.A. - Extrato\n" +
        "03/09/2026 Mercado Horta Fresca -R$ 58,20\n" +
        "06/09/2026 Transporte Metro Leste -R$ 9,80\n" +
        "10/09/2026 Sorveteria Polo Norte -R$ 22,00\n";

    private const string NubankOneLine =
        "Nu Pagamentos S.A. - Extrato03/09/2026 Mercado Horta Fresca -R$ 58,20" +
        "06/09/2026 Transporte Metro Leste -R$ 9,8010/09/2026 Sorveteria Polo Norte -R$ 22,00";

    [Theory]
    [InlineData(NubankLines)]
    [InlineData(NubankOneLine)]
    public void Nubank_ReturnsOneTransactionPerLine(string text)
    {
        var result = new NubankParser().Parse(text);

        Assert.Equal(3, result.Count);
        Assert.Equal(("Mercado Horta Fresca", 58.20m), (result[0].Description, result[0].Amount));
        Assert.Equal(("Transporte Metro Leste", 9.80m), (result[1].Description, result[1].Amount));
        Assert.Equal(("Sorveteria Polo Norte", 22.00m), (result[2].Description, result[2].Amount));
        Assert.Equal(new DateTime(2026, 9, 3), result[0].Date);
        Assert.Equal(new DateTime(2026, 9, 6), result[1].Date);
        Assert.Equal(new DateTime(2026, 9, 10), result[2].Date);
        Assert.All(result, t => Assert.Equal(TransactionType.Debit, t.Type));
    }

    [Fact]
    public void Nubank_OneLinePage_KeepsCreditsAsCredits()
    {
        const string text = "Nu Pagamentos S.A.03/09/2026 Padaria Sol Nascente -R$ 12,50" +
                            "04/09/2026 Reembolso Empresa Ficticia +R$ 1.200,00" +
                            "05/09/2026 Cafeteria Lua Cheia -R$ 7,00";

        var result = new NubankParser().Parse(text);

        Assert.Equal(3, result.Count);
        Assert.Equal(TransactionType.Credit, result[1].Type);
        Assert.Equal(1200.00m, result[1].Amount);
        Assert.Equal("Cafeteria Lua Cheia", result[2].Description);
    }

    // ── C03: Banco do Brasil ───────────────────────────────────────────────

    private const string BancoBrasilLines =
        "Banco do Brasil S.A. - Extrato de conta\n" +
        "04/09/2026 Quitanda Folha Verde 34,60 D\n" +
        "08/09/2026 Barbearia Corte Fino 45,00 D\n" +
        "15/09/2026 Lavanderia Bolha Azul 28,90 D\n";

    private const string BancoBrasilOneLine =
        "Banco do Brasil S.A. - Extrato de conta04/09/2026 Quitanda Folha Verde 34,60 D" +
        "08/09/2026 Barbearia Corte Fino 45,00 D15/09/2026 Lavanderia Bolha Azul 28,90 D";

    [Theory]
    [InlineData(BancoBrasilLines)]
    [InlineData(BancoBrasilOneLine)]
    public void BancoBrasil_ReturnsOneTransactionPerLine(string text)
    {
        var result = new BancoBrasilParser().Parse(text);

        Assert.Equal(3, result.Count);
        Assert.Equal(("Quitanda Folha Verde", 34.60m), (result[0].Description, result[0].Amount));
        Assert.Equal(("Barbearia Corte Fino", 45.00m), (result[1].Description, result[1].Amount));
        Assert.Equal(("Lavanderia Bolha Azul", 28.90m), (result[2].Description, result[2].Amount));
        Assert.Equal(new DateTime(2026, 9, 4), result[0].Date);
        Assert.Equal(new DateTime(2026, 9, 8), result[1].Date);
        Assert.Equal(new DateTime(2026, 9, 15), result[2].Date);
        Assert.All(result, t => Assert.Equal(TransactionType.Debit, t.Type));
    }

    [Fact]
    public void BancoBrasil_OneLinePage_KeepsTheCreditIndicator()
    {
        const string text = "BB S.A.04/09/2026 Quitanda Folha Verde 34,60 D" +
                            "05/09/2026 Deposito Tia Fulana 300,00 C06/09/2026 Barbearia Corte Fino 45,00 D";

        var result = new BancoBrasilParser().Parse(text);

        Assert.Equal(3, result.Count);
        Assert.Equal(TransactionType.Credit, result[1].Type);
        Assert.Equal(300.00m, result[1].Amount);
        Assert.Equal(TransactionType.Debit, result[2].Type);
    }

    // ── C03: Itaú ──────────────────────────────────────────────────────────

    private const string ItauLines =
        "Itau Unibanco S.A. - Extrato mensal\n" +
        "04/09 Peixaria Mar Aberto 61,30-\n" +
        "09/09 Chaveiro Porta Segura 25,00-\n" +
        "16/09 Doceria Mel e Canela 19,75-\n";

    private const string ItauOneLine =
        "Itau Unibanco S.A. - Extrato mensal04/09 Peixaria Mar Aberto 61,30-" +
        "09/09 Chaveiro Porta Segura 25,00-16/09 Doceria Mel e Canela 19,75-";

    [Theory]
    [InlineData(ItauLines)]
    [InlineData(ItauOneLine)]
    public void Itau_ReturnsOneTransactionPerLine(string text)
    {
        var result = new ItauParser().Parse(text);

        Assert.Equal(3, result.Count);
        Assert.Equal(("Peixaria Mar Aberto", 61.30m), (result[0].Description, result[0].Amount));
        Assert.Equal(("Chaveiro Porta Segura", 25.00m), (result[1].Description, result[1].Amount));
        Assert.Equal(("Doceria Mel e Canela", 19.75m), (result[2].Description, result[2].Amount));
        Assert.Equal((9, 4), (result[0].Date.Month, result[0].Date.Day));
        Assert.Equal((9, 9), (result[1].Date.Month, result[1].Date.Day));
        Assert.Equal((9, 16), (result[2].Date.Month, result[2].Date.Day));
        Assert.All(result, t => Assert.Equal(TransactionType.Debit, t.Type));
    }

    [Fact]
    public void Itau_OneLinePage_KeepsCreditsWithoutTheTrailingMinus()
    {
        const string text = "Itau Unibanco S.A.04/09 Peixaria Mar Aberto 61,30-" +
                            "05/09 Pix Recebido Fulano 2.000,0006/09 Chaveiro Porta Segura 25,00-";

        var result = new ItauParser().Parse(text);

        Assert.Equal(3, result.Count);
        Assert.Equal(TransactionType.Credit, result[1].Type);
        Assert.Equal(2000.00m, result[1].Amount);
        Assert.Equal(TransactionType.Debit, result[2].Type);
    }

    [Fact]
    public void Itau_AFullDateInTheHeader_IsNotATransaction()
    {
        const string text = "Itau Unibanco S.A. Extrato de 01/09/2026 a 30/09/2026\n" +
                            "04/09 Peixaria Mar Aberto 61,30-\n";

        var result = new ItauParser().Parse(text);

        Assert.Single(result);
        Assert.Equal("Peixaria Mar Aberto", result[0].Description);
    }

    // ── C04: Inter, installment purchases ──────────────────────────────────

    private const string InterHeader = "Banco Inter S.A.\n";

    [Fact]
    public void Inter_InstallmentPurchase_IsReadWithThePurchaseDate()
    {
        const string text = InterHeader +
            "07/09/2026 Magazine Casa Bela Parcela 03/10 R$ 99,90\n";

        var result = new InterBankParser().Parse(text);

        Assert.Single(result);
        Assert.Equal(new DateTime(2026, 9, 7), result[0].Date);
        Assert.Equal("Magazine Casa Bela Parcela 03/10", result[0].Description);
        Assert.Equal(99.90m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);
    }

    [Fact]
    public void Inter_InstallmentNumbersInTheDescription_AreNotReadAsADate()
    {
        const string text = InterHeader +
            "09/09/2026 Otica Visao Clara 02/06 lentes R$ 150,00\n";

        var result = new InterBankParser().Parse(text);

        Assert.Single(result);
        Assert.Equal(new DateTime(2026, 9, 9), result[0].Date);
        Assert.Equal("Otica Visao Clara 02/06 lentes", result[0].Description);
        Assert.Equal(150.00m, result[0].Amount);
    }

    [Fact]
    public void Inter_BillWithInstallmentsAndRegularLines_ReturnsEveryLine()
    {
        const string text = InterHeader +
            "Fatura do cliente Marina Teste Exemplo\n" +
            "07/09/2026 Magazine Casa Bela Parcela 03/10 R$ 99,90\n" +
            "09/09/2026 Otica Visao Clara 02/06 lentes R$ 150,00\n" +
            "12/09/2026 Papelaria Risco Fino R$ 15,00 D\n" +
            "13/09/2026 Deposito Tia Fulana R$ 300,00 C\n";

        var result = new InterBankParser().Parse(text);

        Assert.Equal(4, result.Count);
        Assert.Equal(new DateTime(2026, 9, 7), result[0].Date);
        Assert.Equal(new DateTime(2026, 9, 9), result[1].Date);
        Assert.Equal(new DateTime(2026, 9, 12), result[2].Date);
        Assert.Equal(TransactionType.Debit, result[2].Type);
        Assert.Equal(TransactionType.Credit, result[3].Type);
        Assert.Equal(300.00m, result[3].Amount);
    }

    [Fact]
    public void Inter_OneLinePage_WithInstallments_ReturnsEveryLine()
    {
        const string text = "Banco Inter S.A.Fatura do cliente Marina Teste Exemplo" +
            "07/09/2026 Magazine Casa Bela Parcela 03/10 R$ 99,90" +
            "09/09/2026 Otica Visao Clara 02/06 lentes R$ 150,00" +
            "12/09/2026 Papelaria Risco Fino R$ 15,00 D13/09/2026 Deposito Tia Fulana R$ 300,00 C";

        var result = new InterBankParser().Parse(text);

        Assert.Equal(4, result.Count);
        Assert.Equal("Magazine Casa Bela Parcela 03/10", result[0].Description);
        Assert.Equal(new DateTime(2026, 9, 9), result[1].Date);
        Assert.Equal("Otica Visao Clara 02/06 lentes", result[1].Description);
        Assert.Equal(TransactionType.Credit, result[3].Type);
    }

    [Fact]
    public void Inter_ShortDateOpeningALine_IsStillATransactionDate()
    {
        // Behaviour that existed before: a line that starts with DD/MM is a dated line.
        const string text = InterHeader +
            "07/09 Mercado Pague Menos R$ 89,50\n";

        var result = new InterBankParser().Parse(text);

        Assert.Single(result);
        Assert.Equal((9, 7), (result[0].Date.Month, result[0].Date.Day));
        Assert.Equal("Mercado Pague Menos", result[0].Description);
    }
}
