using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Parsers;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>
/// Which money value of an entry is the amount, headers on one-line pages, short dates glued to the
/// previous amount, thousands separators. Synthetic texts only (invented merchants, values, dates).
///
/// Rules (same as before the C03/C04 rewrite):
///  - Nubank, Itaú: the amount is the LAST money value of the entry (entry = from its date to the next
///    entry's date or the end of the line). Neither layout has a running-balance column.
///  - Banco do Brasil: the amount is the first value followed by the D/C letter; a second "value D/C"
///    on the line is the running balance and is ignored.
///  - Inter: the amount is the first "R$ value" after the description (as before).
/// </summary>
[Trait("Category", "StatementLayouts")]
public sealed class StatementAmountRulesTests
{
    [Theory]
    [InlineData("Nu Pagamentos S.A.\n03/09/2026 Compra em 3x de R$ 20,00 total -R$ 60,00\n")]
    [InlineData("Nu Pagamentos S.A.03/09/2026 Compra em 3x de R$ 20,00 total -R$ 60,00")]
    public void Nubank_WithTwoMoneyValuesInAnEntry_TakesTheLastOne(string text)
    {
        var result = new NubankParser().Parse(text);

        Assert.Single(result);
        Assert.Equal(60.00m, result[0].Amount);
        Assert.Equal("Compra em 3x de R$ 20,00 total", result[0].Description);
        Assert.Equal(TransactionType.Debit, result[0].Type);
    }

    [Fact]
    public void Nubank_OneLinePage_TwoValuesInTheFirstEntry_DoNotLeakIntoTheNext()
    {
        const string text = "Nu Pagamentos S.A.03/09/2026 Compra em 3x de R$ 20,00 total -R$ 60,00" +
                            "04/09/2026 Padaria Sol Nascente -R$ 12,50";

        var result = new NubankParser().Parse(text);

        Assert.Equal([60.00m, 12.50m], result.Select(t => t.Amount));
    }

    [Theory]
    [InlineData("Itau Unibanco S.A.\n04/09 Compra 3x de 20,00 total 60,00-\n")]
    [InlineData("Itau Unibanco S.A.04/09 Compra 3x de 20,00 total 60,00-")]
    public void Itau_WithTwoMoneyValuesInAnEntry_TakesTheLastOne(string text)
    {
        var result = new ItauParser().Parse(text);

        Assert.Single(result);
        Assert.Equal(60.00m, result[0].Amount);
        Assert.Equal("Compra 3x de 20,00 total", result[0].Description);
    }

    [Fact]
    public void Itau_OneLinePage_WithAShortDateRangeInTheHeader_DoesNotSwallowTheEntries()
    {
        const string text = "Itau Unibanco S.A. Lancamentos de 01/09 a 30/09 Saldo anterior 100,00 " +
                            "04/09 Peixaria Mar Aberto 61,30-09/09 Chaveiro Porta Segura 25,00-";

        var result = new ItauParser().Parse(text);

        Assert.Equal(2, result.Count);
        Assert.Equal("Peixaria Mar Aberto", result[0].Description);
        Assert.Equal(61.30m, result[0].Amount);
        Assert.Equal("Chaveiro Porta Segura", result[1].Description);
        Assert.Equal(25.00m, result[1].Amount);
    }

    [Fact]
    public void Itau_OneLinePage_InstallmentNumbersAreNotDates()
    {
        const string text = "Itau Unibanco S.A.04/09 Loja Alfa Parcela 03/10 50,00-09/09 Loja Beta 25,00-";

        var result = new ItauParser().Parse(text);

        Assert.Equal(2, result.Count);
        Assert.Equal("Loja Alfa Parcela 03/10", result[0].Description);
        Assert.Equal(50.00m, result[0].Amount);
        Assert.Equal(4, result[0].Date.Day);
    }

    [Fact]
    public void Itau_TextAfterTheLastEntryOnItsOwnLine_IsNotAnAmount()
    {
        const string text = "Itau Unibanco S.A.\n04/09 Peixaria Mar Aberto 61,30-\nSaldo final 9.999,99\n";

        var result = new ItauParser().Parse(text);

        Assert.Single(result);
        Assert.Equal(61.30m, result[0].Amount);
    }

    [Fact]
    public void Inter_OneLinePage_ShortDatesGluedToThePreviousAmount_AreStillDates()
    {
        const string text = "Banco Inter S.A.07/09 Loja Alfa Parcela 03/10 R$ 10,00" +
                            "08/09 Loja Beta 02/06 lentes R$ 20,0009/09 Loja Gama R$ 30,00 D" +
                            "10/09 Loja Delta R$ 40,00";

        var result = new InterBankParser().Parse(text);

        Assert.Equal(4, result.Count);
        Assert.Equal(
            ["Loja Alfa Parcela 03/10", "Loja Beta 02/06 lentes", "Loja Gama", "Loja Delta"],
            result.Select(t => t.Description));
        Assert.Equal([10.00m, 20.00m, 30.00m, 40.00m], result.Select(t => t.Amount));
        Assert.Equal([7, 8, 9, 10], result.Select(t => t.Date.Day));
    }

    [Fact]
    public void ThousandsSeparators_OnDebitLines_AreReadInEveryLayout()
    {
        var nubank = new NubankParser().Parse("Nu Pagamentos S.A.\n03/09/2026 Geladeira Loja Norte -R$ 1.234,56\n");
        var bb = new BancoBrasilParser().Parse("Banco do Brasil S.A.\n03/09/2026 Geladeira Loja Norte 1.234,56 D\n");
        var itau = new ItauParser().Parse("Itau Unibanco S.A.\n03/09 Geladeira Loja Norte 1.234,56-\n");
        var inter = new InterBankParser().Parse("Banco Inter S.A.\n03/09/2026 Geladeira Loja Norte R$ 1.234,56\n");

        var all = new[] { Assert.Single(nubank), Assert.Single(bb), Assert.Single(itau), Assert.Single(inter) };
        Assert.All(all, t => Assert.Equal(1234.56m, t.Amount));
        Assert.All(all, t => Assert.Equal(TransactionType.Debit, t.Type));
    }

    [Fact]
    public void BancoBrasil_WithATrailingBalanceColumn_TakesTheValueFollowedByTheFirstIndicator()
    {
        const string text = "Banco do Brasil S.A.\n04/09/2026 Quitanda Folha Verde 34,60 D 1.200,00 C\n";

        var result = new BancoBrasilParser().Parse(text);

        Assert.Single(result);
        Assert.Equal(34.60m, result[0].Amount);
        Assert.Equal(TransactionType.Debit, result[0].Type);
    }
}
