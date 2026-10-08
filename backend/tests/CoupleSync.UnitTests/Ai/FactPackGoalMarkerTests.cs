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

    // Review 2: only the title exactly as an answer shows it (between quotes, trimmed) becomes the marker again.
    [Theory]
    [InlineData("Falta pouco para \"Casa   na praia\" e para \"Casa\".", "Falta pouco para {{g2}} e para {{g1}}.")]
    [InlineData("Falta pouco para \"Carro (2027)\", \"Ação\" e \"Sítio\".", "Falta pouco para {{g3}}, {{g5}} e uma meta.")]
    // Not what the system wrote: no quotes, another case, a piece of the title, something else between quotes.
    [InlineData("A casa na praia, \"casa\", \"Casa na\" e \"Alimentação\".", "A casa na praia, \"casa\", \"Casa na\" e \"Alimentação\".")]
    [InlineData("", "")]
    public void InAnAnswerThatComesBack_OnlyTheShownTitle_BecomesItsMarker(string text, string expected)
        => Assert.Equal(expected, FactPackPrivacyFilter.MaskShownGoalTitles(text, Titles, ["Sítio", "Casa", " "]));

    [Theory]
    [InlineData("casa na\n praia e CASA", "casa na\n praia={{g2}}|CASA={{g1}}")]
    [InlineData("O casamento e as casas não são a meta.", "")]
    [InlineData("Quanto falta para o carro (2027)?", "carro (2027)={{g3}}")]
    // Decomposed text: the combining accents fold to nothing and go with their letters.
    [InlineData("E a ação? E a casa, e a casa?", "ação={{g5}}|casa={{g1}}")]
    [InlineData("", "")]
    public void TheTitlesCitedInAQuestion_AreFoundAsTyped_OncePerTitle_InTheOrderTheyAppear(string text, string expected)
        => Assert.Equal(
            expected,
            string.Join("|", FactPackPrivacyFilter.FindGoalMentions(text, Titles).Select(m => $"{m.Text}={string.Join("+", m.Markers)}")));

    [Fact]
    public void WithNoGoals_NothingIsFound()
        => Assert.Empty(FactPackPrivacyFilter.FindGoalMentions("Quanto gastamos?", new Dictionary<string, string>()));

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
