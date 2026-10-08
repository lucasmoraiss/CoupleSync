using CoupleSync.Application.Ai;

namespace CoupleSync.UnitTests.Ai;

/// <summary>
/// Issue #38, review 1 — the titles of the goals never leave the API (they go as {{g1}}, {{g2}}) and no marker
/// reaches the person raw: the edges of <see cref="FactPackPrivacyFilter"/> that the Assistant tests do not walk.
/// </summary>
[Trait("Category", "Ai")]
public sealed class FactPackGoalMarkerTests
{
    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["g1"] = "Casa",
        ["g2"] = "  Casa   na praia ",
        ["g3"] = "Carro (2027)",
        ["g4"] = "   ",
        ["g5"] = "Ação",
    };

    [Theory]
    [InlineData("Falta pouco para \"Casa na praia\".", "Falta pouco para {{g2}}.")]
    [InlineData("casa na\n praia e CASA", "{{g2}} e {{g1}}")]
    [InlineData("O casamento e as casas não são a meta.", "O casamento e as casas não são a meta.")]
    [InlineData("Quanto falta para o carro (2027)?", "Quanto falta para o {{g3}}?")]
    // Decomposed text: the combining accents fold to nothing and go away with their letters.
    [InlineData("E a ação?", "E a {{g5}}?")]
    [InlineData("", "")]
    public void EveryKnownTitle_BecomesItsMarker(string text, string expected)
        => Assert.Equal(expected, FactPackPrivacyFilter.ReplaceGoalTitles(text, Titles));

    [Fact]
    public void WithNoGoals_TheTextIsUntouched()
        => Assert.Equal("Quanto gastamos?", FactPackPrivacyFilter.ReplaceGoalTitles("Quanto gastamos?", new Dictionary<string, string>()));

    [Theory]
    [InlineData("Faltam R$ 10,00 para {{g1}} e {{B}}.", false)]
    [InlineData("Faltam R$ 10,00 para { G 1 }.", false)]
    [InlineData("Sem marcador nenhum.", false)]
    [InlineData("Faltam R$ 10,00 para {{meta1}}.", true)]
    [InlineData("Faltam R$ 10,00 para {{g1}}}.", true)]
    [InlineData("Faltam R$ 10,00 para {{.", true)]
    [InlineData("Faltam R$ 10,00 para }.", true)]
    public void WhatIsLeftOfAMarker_IsFound(string text, bool leftover)
        => Assert.Equal(leftover, FactPackPrivacyFilter.HasMarkerLeftovers(text));
}
