using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.UnitTests.OpenFinance;

/// <summary>
/// Issue #25 — the category Pluggy gives a transaction becomes one of the seven keys of the app. The table is locked
/// here: changing a line of it is a decision, and this test has to change with it.
/// </summary>
[Trait("Category", "OpenFinance")]
public sealed class PluggyCategoryMapTests
{
    [Theory]
    [InlineData("Alimentação", "ALIMENTACAO")]
    [InlineData("Transporte", "TRANSPORTE")]
    [InlineData("Saúde", "SAUDE")]
    [InlineData("Moradia", "MORADIA")]
    [InlineData("Compras", "COMPRAS")]
    [InlineData("Lazer", "LAZER")]
    public void ThePortugueseNamesOfPluggy_GoToTheKeysOfTheApp(string pluggyName, string expected)
    {
        Assert.Equal(expected, PluggyCategoryMap.Map(null, pluggyName));
        // Any case, with or without accents and spaces around.
        Assert.Equal(expected, PluggyCategoryMap.Map(null, $"  {pluggyName.ToUpperInvariant()} "));
        Assert.Equal(expected, PluggyCategoryMap.Map("", AccentFolding.RemoveAccents(pluggyName).ToLowerInvariant()));
    }

    [Theory]
    [InlineData("11000000", "Food and drinks", "ALIMENTACAO")]
    [InlineData("11010000", "Eating out", "ALIMENTACAO")]
    [InlineData("11020000", "Food delivery", "ALIMENTACAO")]
    [InlineData("10000000", "Groceries", "ALIMENTACAO")]
    [InlineData("19000000", "Transportation", "TRANSPORTE")]
    [InlineData("19010000", "Taxi and ride-hailing", "TRANSPORTE")]
    [InlineData("19050100", "Gas stations", "TRANSPORTE")]
    [InlineData("20040000", "Vehicle insurance", "TRANSPORTE")]
    [InlineData("18000000", "Healthcare", "SAUDE")]
    [InlineData("18020000", "Pharmacy", "SAUDE")]
    [InlineData("07030100", "Gyms and fitness centers", "SAUDE")]
    [InlineData("20030000", "Health insurance", "SAUDE")]
    [InlineData("17000000", "Housing", "MORADIA")]
    [InlineData("17010000", "Rent", "MORADIA")]
    [InlineData("17020200", "Electricity", "MORADIA")]
    [InlineData("07010100", "Internet", "MORADIA")]
    [InlineData("20020000", "Home insurance", "MORADIA")]
    [InlineData("08000000", "Shopping", "COMPRAS")]
    [InlineData("08010000", "Online shopping", "COMPRAS")]
    [InlineData("21000000", "Leisure", "LAZER")]
    [InlineData("09020000", "Video streaming", "LAZER")]
    [InlineData("07040300", "Cinema, theater and concerts", "LAZER")]
    [InlineData("12010000", "Airport and airlines", "LAZER")]
    public void TheCodesOfPluggy_GoToTheKeysOfTheApp(string categoryId, string categoryName, string expected)
    {
        Assert.Equal(expected, PluggyCategoryMap.Map(categoryId, categoryName));
        // The code decides on its own: a name the table has never seen changes nothing.
        Assert.Equal(expected, PluggyCategoryMap.Map(categoryId, "Some new name"));
        Assert.Equal(expected, PluggyCategoryMap.Map($" {categoryId} ", null));
    }

    [Theory]
    [InlineData("01010000", "Salary")]
    [InlineData("05070000", "Transfer - PIX")]
    [InlineData("03020000", "Fixed income")]
    [InlineData("15010000", "Income taxes")]
    [InlineData("16010000", "Account fees")]
    [InlineData("07020200", "University")]
    [InlineData("20010000", "Life insurance")]
    [InlineData("99999999", "Other")]
    [InlineData(null, "Qualquer outra coisa")]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("xx", "   ")]
    public void AnyOtherCategory_IsOutros(string? categoryId, string? categoryName)
    {
        Assert.Null(PluggyCategoryMap.TryMap(categoryId, categoryName));
        Assert.Equal(TransactionCategories.Other, PluggyCategoryMap.Map(categoryId, categoryName));
    }

    [Fact]
    public void WithoutAKnownCode_TheNameDecides_InEnglishToo()
    {
        Assert.Equal("ALIMENTACAO", PluggyCategoryMap.Map("99999999", "Food and drinks"));
        Assert.Equal("TRANSPORTE", PluggyCategoryMap.Map(null, "transportation"));
        Assert.Equal("SAUDE", PluggyCategoryMap.Map(null, "Healthcare"));
        Assert.Equal("MORADIA", PluggyCategoryMap.Map(null, "Housing"));
        Assert.Equal("COMPRAS", PluggyCategoryMap.Map(null, "Shopping"));
        Assert.Equal("LAZER", PluggyCategoryMap.Map(null, "Leisure"));
    }

    [Fact]
    public void TheCodeWinsOverTheName()
        => Assert.Equal("SAUDE", PluggyCategoryMap.Map("18020000", "Shopping"));

    [Fact]
    public void EveryValueOfTheTable_IsAKeyOfTheApp_AndNeverOutros()
    {
        var keys = TransactionCategories.All.Select(c => c.Key).Where(k => k != TransactionCategories.Other).ToHashSet();

        Assert.All(PluggyCategoryMap.ByIdPrefix.Values, value => Assert.Contains(value, keys));
        Assert.All(PluggyCategoryMap.ByName.Values, value => Assert.Contains(value, keys));
        // The six categories that are not OUTROS are all reachable.
        Assert.Equal(keys, PluggyCategoryMap.ByIdPrefix.Values.ToHashSet());
        Assert.Equal(keys, PluggyCategoryMap.ByName.Values.ToHashSet());
        // Names are stored folded, as the lookup folds what Pluggy sends.
        Assert.All(PluggyCategoryMap.ByName.Keys, name => Assert.Equal(TransactionCategories.Fold(name), name));
        Assert.All(PluggyCategoryMap.ByIdPrefix.Keys, prefix => Assert.True(prefix.Length is 2 or 4 or 6 && prefix.All(char.IsAsciiDigit)));
    }

    [Fact]
    public void TheTable_IsLocked()
    {
        var byId = string.Join(";", PluggyCategoryMap.ByIdPrefix.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        Assert.Equal(
            "0701=MORADIA;0703=SAUDE;0704=LAZER;08=COMPRAS;09=LAZER;10=ALIMENTACAO;11=ALIMENTACAO;12=LAZER;17=MORADIA;"
            + "18=SAUDE;19=TRANSPORTE;2002=MORADIA;2003=SAUDE;2004=TRANSPORTE;21=LAZER",
            byId);

        var byName = string.Join(";", PluggyCategoryMap.ByName.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        Assert.Equal(
            "ALIMENTACAO=ALIMENTACAO;ALIMENTOS E BEBIDAS=ALIMENTACAO;ALUGUEL=MORADIA;AUTOMOTIVE=TRANSPORTE;COMPRAS=COMPRAS;"
            + "DIGITAL SERVICES=LAZER;EATING OUT=ALIMENTACAO;ENTERTAINMENT=LAZER;FARMACIA=SAUDE;FOOD AND DRINKS=ALIMENTACAO;"
            + "FOOD DELIVERY=ALIMENTACAO;GAS STATIONS=TRANSPORTE;GROCERIES=ALIMENTACAO;HEALTH=SAUDE;HEALTHCARE=SAUDE;"
            + "HOUSING=MORADIA;LAZER=LAZER;LEISURE=LAZER;MERCADO=ALIMENTACAO;MORADIA=MORADIA;ONLINE SHOPPING=COMPRAS;"
            + "PHARMACY=SAUDE;PUBLIC TRANSPORTATION=TRANSPORTE;RENT=MORADIA;RESTAURANTES=ALIMENTACAO;SAUDE=SAUDE;"
            + "SHOPPING=COMPRAS;SUPERMERCADO=ALIMENTACAO;TAXI AND RIDE-HAILING=TRANSPORTE;TRANSPORTATION=TRANSPORTE;"
            + "TRANSPORTE=TRANSPORTE;TRAVEL=LAZER;UTILITIES=MORADIA;VIAGEM=LAZER",
            byName);
    }
}
