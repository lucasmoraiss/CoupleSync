using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.UnitTests.Domain;

public sealed class TransactionCategoriesTests
{
    [Fact]
    public void CanonicalList_IsTheSevenKeysOfTheAppPicker_WithOutrosLast()
    {
        Assert.Equal(
            ["ALIMENTACAO", "TRANSPORTE", "COMPRAS", "SAUDE", "LAZER", "MORADIA", "OUTROS"],
            TransactionCategories.All.Select(c => c.Key));
        Assert.Equal(
            ["Alimentação", "Transporte", "Compras", "Saúde", "Lazer", "Moradia", "Outros"],
            TransactionCategories.All.Select(c => c.Label));
        Assert.Equal("OUTROS", TransactionCategories.Other);
    }

    [Fact]
    public void Keys_AreUpperCaseAsciiAndUnique()
    {
        Assert.All(TransactionCategories.All, c => Assert.Matches("^[A-Z]+$", c.Key));
        Assert.Equal(TransactionCategories.All.Count, TransactionCategories.All.Select(c => c.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("ALIMENTACAO", "ALIMENTACAO")]
    [InlineData("Alimentação", "ALIMENTACAO")]
    [InlineData("alimentação", "ALIMENTACAO")]
    [InlineData("ALIMENTAÇÃO", "ALIMENTACAO")]
    [InlineData("alimentacao", "ALIMENTACAO")]
    [InlineData("Alimentacao", "ALIMENTACAO")]
    [InlineData("  Alimentação  ", "ALIMENTACAO")]
    [InlineData("\talimentacao\n", "ALIMENTACAO")]
    [InlineData("Saúde", "SAUDE")]
    [InlineData("SAÚDE", "SAUDE")]
    [InlineData("saude", "SAUDE")]
    [InlineData("Transporte", "TRANSPORTE")]
    [InlineData("transporte", "TRANSPORTE")]
    [InlineData("Compras", "COMPRAS")]
    [InlineData("Lazer", "LAZER")]
    [InlineData("lazer", "LAZER")]
    [InlineData("Moradia", "MORADIA")]
    [InlineData("MORADIA", "MORADIA")]
    [InlineData("Outros", "OUTROS")]
    [InlineData("outros", "OUTROS")]
    [InlineData("OUTROS", "OUTROS")]
    public void TryNormalize_ConvergesCaseAccentsAndEdgeSpacesToTheKey(string input, string expected)
        => Assert.Equal(expected, TransactionCategories.TryNormalize(input));

    [Fact]
    public void TryNormalize_DecomposedAccents_AreAccepted()
    {
        // "Alimentação" written with combining marks (NFD), as some keyboards/clients produce it.
        var decomposed = "Alimentação";
        Assert.Equal("ALIMENTACAO", TransactionCategories.TryNormalize(decomposed));
        Assert.Equal("SAUDE", TransactionCategories.TryNormalize("Saúde"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mercado")]
    [InlineData("Educação")]
    [InlineData("Vestuário")]
    [InlineData("Serviços")]
    [InlineData("Investimentos")]
    [InlineData("Food")]
    [InlineData("Alimentação extra")]
    [InlineData("Alimen tacao")]
    [InlineData("ALIMENTACAO1")]
    [InlineData("Alimentação/Mercado")]
    public void TryNormalize_UnknownOrBlank_ReturnsNull(string? input)
        => Assert.Null(TransactionCategories.TryNormalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Educação")]
    [InlineData("Mercado")]
    [InlineData("qualquer coisa")]
    public void NormalizeOrOther_UnknownBecomesOutros(string? input)
        => Assert.Equal("OUTROS", TransactionCategories.NormalizeOrOther(input));

    [Fact]
    public void EveryKeyAndLabel_NormalizesToItsOwnKey_InEveryVariant()
    {
        foreach (var category in TransactionCategories.All)
        {
            foreach (var spelling in new[] { category.Key, category.Label })
            {
                var variants = new[]
                {
                    spelling,
                    spelling.ToLowerInvariant(),
                    spelling.ToUpperInvariant(),
                    $"  {spelling}",
                    $"{spelling}   ",
                    $" {spelling.ToLowerInvariant()} ",
                };

                foreach (var variant in variants)
                    Assert.Equal(category.Key, TransactionCategories.NormalizeOrOther(variant));
            }
        }
    }

    [Fact]
    public void Normalization_IsIdempotent()
    {
        foreach (var input in new[] { "Alimentação", "x", "", " Saúde ", "OUTROS" })
        {
            var once = TransactionCategories.NormalizeOrOther(input);
            Assert.Equal(once, TransactionCategories.NormalizeOrOther(once));
        }
    }

    [Fact]
    public void Label_ReturnsThePortugueseLabel_OfAnySpelling()
    {
        Assert.Equal("Alimentação", TransactionCategories.Label("ALIMENTACAO"));
        Assert.Equal("Saúde", TransactionCategories.Label("saude"));
        Assert.Equal("Outros", TransactionCategories.Label("desconhecida"));
    }

    [Fact]
    public void InvalidMessage_IsPortugueseAndListsEveryAcceptedKey()
    {
        Assert.StartsWith("Categoria inválida", TransactionCategories.InvalidMessage);
        foreach (var category in TransactionCategories.All)
            Assert.Contains(category.Key, TransactionCategories.InvalidMessage);
    }
}
