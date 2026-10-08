using System.Text.Json;
using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.UnitTests.Ai;

/// <summary>
/// Issue #37 — the text rules of the AI layer (design sections 2.6, 2.7, 3.8 and 3.9). This file is also compiled
/// into the globalization-invariant test project: every rule here compares text without accents and must behave the
/// same with and without ICU. All names, documents and merchants are invented.
/// </summary>
[Trait("Category", "Ai")]
public sealed class AiTextRulesTests
{
    // ---------------------------------------------------------------- NumberGroundingValidator (2.7)

    private const string PackJson = """
        {
          "v": 1,
          "period": { "kind": "week", "from": "2026-10-05" },
          "spend": { "total": 1840.30, "delta_pct": 13.5 },
          "recurring": { "n": 3 },
          "merchants": [ { "id": "m1", "m": "posto 24h" }, { "id": "m2", "m": "padaria" } ]
        }
        """;

    private static JsonElement Pack() => JsonDocument.Parse(PackJson).RootElement;

    private static readonly string[] Refs = ["m1"];

    [Theory]
    [InlineData(1, "Vocês gastaram R$ 1.840,30")]
    [InlineData(2, "cerca de R$ 1.840")]
    [InlineData(3, "R$ 1,8 mil")]
    [InlineData(4, "alta de 13,5%")]
    [InlineData(5, "quase 14%")]
    [InlineData(6, "3 assinaturas")]
    [InlineData(7, "três assinaturas")]
    [InlineData(8, "em 05/10")]
    [InlineData(9, "no Posto 24h")]
    [InlineData(10, "100% do CDI")]
    public void NumberGrounding_PassesTheTenGroundedCasesOfTheDesign(int number, string text)
    {
        var result = NumberGroundingValidator.Validate(text, Pack(), Refs);

        Assert.True(result.IsValid, $"case {number} should pass, but failed with {result.Reason}");
    }

    [Theory]
    [InlineData(11, "R$ 1.900,00")]
    [InlineData(12, "alta de 20%")]
    [InlineData(13, "cinco assinaturas")]
    [InlineData(14, "em 15/11")]
    [InlineData(15, "R$ 2 mil")]
    public void NumberGrounding_RejectsTheFiveInventedCasesOfTheDesign(int number, string text)
    {
        var result = NumberGroundingValidator.Validate(text, Pack(), Refs);

        Assert.False(result.IsValid, $"case {number} should be rejected");
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void NumberGrounding_TheNumberInsideAMerchantName_OnlyPassesWhenTheMerchantIsInRefs()
    {
        Assert.True(NumberGroundingValidator.Validate("no Posto 24h", Pack(), ["m1"]).IsValid);
        Assert.False(NumberGroundingValidator.Validate("no Posto 24h", Pack(), ["m2"]).IsValid);
    }

    [Theory]
    [InlineData("em 05/10/2026")]
    [InlineData("em 10/2026")]
    [InlineData("em outubro de 2026")]
    [InlineData("em 2026")]
    [InlineData("gastos de {{A}} e da meta {{g1}}")]
    [InlineData("um gasto, duas contas, o dobro e metade")]
    [InlineData("2 milhões é muito mais do que isso; aqui são 0, 1 ou 2")]
    public void NumberGrounding_AcceptsTheOtherAllowedForms(string text)
    {
        var pack = JsonDocument.Parse("""{ "d": "2026-10-05", "big": 2000000 }""").RootElement;

        Assert.True(NumberGroundingValidator.Validate(text, pack, []).IsValid, text);
    }

    [Theory]
    [InlineData("em 06/10/2026")]
    [InlineData("em 11/2026")]
    [InlineData("em 2027")]
    [InlineData("R$ 2 mil")] // the year 2026 of the pack is not an amount
    [InlineData("vinte lançamentos")]
    public void NumberGrounding_RejectsDatesAndCountsThatAreNotInThePack(string text)
    {
        var pack = JsonDocument.Parse("""{ "d": "2026-10-05" }""").RootElement;

        Assert.False(NumberGroundingValidator.Validate(text, pack, []).IsValid, text);
    }

    [Fact]
    public void NumberGrounding_InTheAssistant_AlsoAllowsTheNumbersOfTheConversation()
    {
        var pack = JsonDocument.Parse("{}").RootElement;

        Assert.False(NumberGroundingValidator.Validate("Com R$ 350,00 dá.", pack, []).IsValid);
        Assert.True(NumberGroundingValidator.Validate("Com R$ 350,00 dá.", pack, [], ["E se eu guardar R$ 350 por mês?"]).IsValid);
    }

    // ---------------------------------------------------------------- OutputSafetyValidator (2.7)

    [Theory]
    [InlineData("Veja em https://exemplo.test/oferta")]
    [InlineData("Veja em http://exemplo.test")]
    [InlineData("Entre em www.exemplo")]
    [InlineData("Compare em lojaexemplo.com.br")]
    [InlineData("Baixe em exemplo.app")]
    [InlineData("Escreva para ajuda@exemplo.test")]
    [InlineData("Fale com @usuario")]
    [InlineData("Ligue: (11) 99999-9999")]
    [InlineData("O número é 11999999999")]
    [InlineData("Central 0800 000 0000")]
    [InlineData("CPF 000.000.001-91")]
    [InlineData("Use a chave 3f2b8c1e-1111-4222-8333-abcdefabcdef")]
    [InlineData("Entre em contato com o banco")]
    [InlineData("ENTRE EM CONTATO")]
    [InlineData("Clique aqui para ver")]
    [InlineData("Informe a chave Pix")]
    [InlineData("Faça um pix para essa pessoa")]
    [InlineData("Me chame no WhatsApp")]
    [InlineData("Ligue para a central")]
    [InlineData("Acesse o site do banco")]
    [InlineData("Acesse o link")]
    [InlineData("Está no link abaixo")]
    [InlineData("Confirme sua senha")]
    [InlineData("Digite o código de verificação")]
    [InlineData("Deposite o valor hoje")]
    public void OutputSafety_RejectsLinksContactsAndCallsToAction(string text)
    {
        var result = OutputSafetyValidator.Validate(text);

        Assert.False(result.IsValid, text);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData("a conta de telefone subiu")]
    [InlineData("pago por boleto")]
    [InlineData("Vocês gastaram R$ 1.840,30 em outubro, 13,5% a mais.")]
    [InlineData("{{A}} gastou mais com alimentação do que {{B}}.")]
    [InlineData("O gasto com lazer caiu. Parabéns!")]
    public void OutputSafety_AcceptsOrdinaryText(string text)
    {
        var result = OutputSafetyValidator.Validate(text);

        Assert.True(result.IsValid, $"{text}: {result.Reason}");
    }

    [Fact]
    public void OutputSafety_RejectsAMerchantOfThePackThatIsNotInRefs()
    {
        Assert.False(OutputSafetyValidator.Validate("O gasto no Posto 24h subiu.", Pack(), ["m2"]).IsValid);
        Assert.False(OutputSafetyValidator.Validate("O gasto no Posto 24h subiu.", Pack(), []).IsValid);
        Assert.True(OutputSafetyValidator.Validate("O gasto no Posto 24h subiu.", Pack(), ["m1"]).IsValid);
        Assert.True(OutputSafetyValidator.Validate("O gasto na PADARIA subiu.", Pack(), ["m1", "m2"]).IsValid);
        Assert.True(OutputSafetyValidator.Validate("O gasto com combustível subiu.", Pack(), []).IsValid);
    }

    // ---------------------------------------------------------------- FactPackPrivacyFilter, free text (3.8)

    private static readonly AiPerson[] People =
    [
        new("A", "Mariana Souza Lima"),
        new("B", "João da Conceição"),
    ];

    [Fact]
    public void PrivacyFilter_RemovesMemberNamesDocumentsAndContacts()
    {
        const string question =
            "Quanto a Mariana Souza gastou? E o JOAO conceicao? Meu CPF é 000.000.001-91, o CNPJ 00.000.000/0001-91, " +
            "telefone (11) 98765-4321, e-mail mariana.souza@exemplo.test, chave 3f2b8c1e-1111-4222-8333-abcdefabcdef " +
            "e a conta 12345678.";

        var filtered = FactPackPrivacyFilter.FilterFreeText(question, People);

        foreach (var forbidden in new[]
                 {
                     "Mariana", "mariana", "Souza", "JOAO", "conceicao", "000.000.001-91", "00.000.000/0001-91", "98765",
                     "4321", "exemplo.test", "@", "3f2b8c1e", "abcdefabcdef", "12345678",
                 })
            Assert.DoesNotContain(forbidden, filtered);

        Assert.Contains("{{A}}", filtered);
        Assert.Contains("{{B}}", filtered);
        Assert.Contains("[removido]", filtered);
        Assert.StartsWith("Quanto a {{A}} gastou? E o {{B}}?", filtered);
    }

    [Fact]
    public void PrivacyFilter_MatchesNamesWithoutAccents_AndKeepsOtherWords()
    {
        var filtered = FactPackPrivacyFilter.FilterFreeText("joão e Conceição gastaram R$ 1.840,30 em 05/10/2026 da conta", People);

        Assert.Equal("{{B}} e {{B}} gastaram R$ 1.840,30 em 05/10/2026 da conta", filtered);
    }

    [Fact]
    public void PrivacyFilter_PutsTheFirstNamesBackWhenAnsweringTheApp()
    {
        var restored = FactPackPrivacyFilter.RestoreNames("{{A}} gastou mais do que {{B}}; {{C}} não existe.", People);

        Assert.Equal("Mariana gastou mais do que João; alguém do grupo não existe.", restored);
    }

    // ---------------------------------------------------------------- PromptText (2.6, 3.9)

    [Fact]
    public void PromptText_Sanitize_RemovesQuotesControlCharactersAndLineBreaks_AndCutsAtSixty()
    {
        var dirty = "Loja \"\"\" do\r\nbairro\t\u0001\u007f" + new string('x', 100);

        var clean = PromptText.Sanitize(dirty);

        Assert.Equal(60, clean.Length);
        Assert.StartsWith("Loja do bairro xxx", clean);
        Assert.DoesNotContain("\"\"\"", clean);
        Assert.DoesNotContain(clean, c => char.IsControl(c));
    }

    [Fact]
    public void PromptText_Sanitize_AcceptsALongerLimitForTheQuestion()
    {
        Assert.Equal(2000, PromptText.Sanitize(new string('a', 2500), 2000).Length);
        Assert.Equal("ok", PromptText.Sanitize("  ok  "));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 1)]
    [InlineData("abcd", 2)]
    [InlineData("1234567", 2)]
    [InlineData("12345678", 3)]
    public void PromptText_EstimateTokens_IsCharactersDividedByThreeAndAHalfRoundedUp(string text, int expected)
        => Assert.Equal(expected, PromptText.EstimateTokens(text));

    // ---------------------------------------------------------------- history cut (3.9)

    [Fact]
    public void HistoryCut_KeepsTheMostRecentWholeMessagesUpToOneThousandFiveHundredTokens()
    {
        var history = Enumerable.Range(1, 20)
            .Select(i => new LlmMessage(i % 2 == 1 ? "user" : "model", $"{i:D2}" + new string('h', 1998)))
            .ToList();

        var kept = ChatHistoryTrimmer.Trim(history);

        // 2,000 characters are 572 estimated tokens: two fit (1,144), the third would not (1,716).
        Assert.Equal(2, kept.Count);
        Assert.StartsWith("19", kept[0].Text);
        Assert.StartsWith("20", kept[1].Text);
        Assert.All(kept, m => Assert.Equal(2000, m.Text.Length));
        Assert.True(kept.Sum(m => PromptText.EstimateTokens(m.Text)) <= ChatHistoryTrimmer.MaxHistoryTokens);
    }

    [Fact]
    public void HistoryCut_KeepsEverythingThatFits_AndNeverSkipsOverAMessage()
    {
        var small = new List<LlmMessage> { new("user", "oi"), new("model", "olá"), new("user", "tudo bem?") };
        Assert.Equal(small, ChatHistoryTrimmer.Trim(small));

        // The most recent does not fit: nothing older is sent in its place.
        var blocked = new List<LlmMessage> { new("user", "antiga"), new("model", new string('x', 6000)) };
        Assert.Empty(ChatHistoryTrimmer.Trim(blocked));
    }
}
