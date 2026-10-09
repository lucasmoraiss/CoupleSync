using CoupleSync.Application.AiFacts;

namespace CoupleSync.UnitTests.AiFacts;

/// <summary>
/// Issue #39 — the key of an establishment (design 3.3, item 1) and the rule of the transfer to a person (3.8).
/// Establishments are made up or generic.
/// </summary>
public sealed class MerchantKeyTests
{
    [Theory]
    [InlineData("NETFLIX.COM 866-579", "netflix")]
    [InlineData("Netflix.com", "netflix")]
    [InlineData("PG *SPOTIFY SAO PAULO BR", "spotify")]
    [InlineData("MAGAZINE 03/10", "magazine")]
    [InlineData("MAGAZINE PARC 03/10", "magazine")]
    [InlineData("Loja.com.br", "loja")]
    [InlineData("PAG*LojaExemplo", "lojaexemplo")]
    [InlineData("MP *MERCADINHO", "mercadinho")]
    [InlineData("MERCADOPAGO*LOJA EXEMPLO", "loja exemplo")]
    [InlineData("PAGSEGURO *CAFE EXEMPLO", "cafe exemplo")]
    [InlineData("PICPAY*BARBEARIA", "barbearia")]
    [InlineData("IFD*RESTAURANTE EXEMPLO", "restaurante exemplo")]
    [InlineData("EC *PADARIA EXEMPLO", "padaria exemplo")]
    [InlineData("  Padaria   Exemplo  ", "padaria exemplo")]
    [InlineData("POSTO EXEMPLO 0012345 CURITIBA PR BRA", "posto exemplo")]
    [InlineData("APPLE.COM/BILL", "apple bill")]
    [InlineData("LOJA 12", "loja 12")]
    public void Normalize_GivesTheKeyOfTheEstablishment(string merchant, string expected)
        => Assert.Equal(expected, MerchantKey.Normalize(merchant));

    [Theory]
    [InlineData("Pão de Açúcar", "pao de acucar")]
    [InlineData("FARMÁCIA SÃO JOÃO", "farmacia sao joao")]
    [InlineData("Açaí & Cia", "acai cia")]
    // Decomposed text (letter + combining mark), as some keyboards and files send it.
    [InlineData("Café Exemplo", "cafe exemplo")]
    public void Normalize_RemovesAccentsByTheExplicitTable(string merchant, string expected)
        => Assert.Equal(expected, MerchantKey.Normalize(merchant));

    /// <summary>Trap 1 of CLAUDE.md: nothing in this context may depend on ICU (Unicode normalization, named cultures).</summary>
    [Fact]
    public void TheAiFactsSources_NeverUseUnicodeNormalizationNorANamedCulture()
    {
        var directory = FindDirectory("backend", "src", "CoupleSync.Application", "AiFacts");
        var files = Directory.GetFiles(directory, "*.cs");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("NormalizationForm", source);
            Assert.DoesNotContain("IsNormalized", source);
            Assert.DoesNotContain("new CultureInfo(", source);
            Assert.DoesNotContain("ToLower()", source);
            Assert.DoesNotContain("ToUpper()", source);
            Assert.DoesNotMatch(@"\.Normalize\(\s*\)", source);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123456")]
    public void Normalize_OfNothingUseful_IsEmpty(string? merchant)
        => Assert.Equal(string.Empty, MerchantKey.Normalize(merchant));

    [Fact]
    public void Normalize_NeverRemovesTheWholeNameAsALocation()
    {
        Assert.Equal("sp", MerchantKey.Normalize("SP"));
        Assert.Equal("santos", MerchantKey.Normalize("SANTOS"));
    }

    [Fact]
    public void TheAcquirerPrefixes_AreTheLockedList()
        => Assert.Equal(["pg *", "pag*", "mp *", "mercadopago*", "pagseguro", "picpay*", "ifd*", "ec *"], MerchantKey.AcquirerPrefixes);

    [Fact]
    public void TheLocationSuffixes_AreTheLockedLists()
    {
        Assert.Equal(["br", "bra", "brasil", "brazil"], MerchantKey.CountrySuffixes);
        Assert.Equal(27, MerchantKey.StateSuffixes.Count);
        Assert.Equal(
            "ac,al,am,ap,ba,ce,df,es,go,ma,mg,ms,mt,pa,pb,pe,pi,pr,rj,rn,ro,rr,rs,sc,se,sp,to",
            string.Join(',', MerchantKey.StateSuffixes));
        Assert.Equal(
            "sao paulo|rio de janeiro|belo horizonte|brasilia|salvador|fortaleza|curitiba|manaus|recife|porto alegre|goiania|belem|"
            + "guarulhos|campinas|sao luis|maceio|natal|campo grande|teresina|joao pessoa|osasco|barueri|santo andre|"
            + "sao bernardo do campo|ribeirao preto|sorocaba|uberlandia|florianopolis|vitoria|cuiaba|aracaju|niteroi|santos|"
            + "sao jose dos campos|londrina|joinville",
            string.Join('|', MerchantKey.CitySuffixes));
    }

    [Fact]
    public void TheSubscriptionHints_AreTheLockedList()
    {
        Assert.Equal(
            "netflix|spotify|disney|hbo max|prime video|amazon prime|globoplay|deezer|youtube|apple bill|google play|google one|"
            + "google storage|icloud|microsoft|adobe|openai|chatgpt|paramount|crunchyroll|telecine|premiere|dropbox|canva|linkedin|"
            + "duolingo|tidal|twitch|xbox|playstation|nintendo|kindle|audible|github|notion",
            string.Join('|', MerchantHints.SubscriptionMerchantHints));
        Assert.Equal(["max"], MerchantHints.SubscriptionExactHints);
    }

    [Fact]
    public void TheUtilityHints_AreTheLockedList()
        => Assert.Equal(
            "enel|cemig|copel|cpfl|light|neoenergia|equatorial|energisa|celesc|coelba|celpe|sabesp|cedae|copasa|sanepar|embasa|"
            + "caesb|corsan|comgas|naturgy|ultragaz|supergasbras|liquigas|vivo|claro|tim|oi|sky|algar|brisanet|luz|energia|agua|gas|"
            + "internet|telefone|celular|condominio|aluguel",
            string.Join('|', MerchantHints.UtilityMerchantHints));

    [Theory]
    [InlineData("netflix", true)]
    [InlineData("google play youtube", true)]
    [InlineData("max", true)]
    [InlineData("max atacadista", false)]
    [InlineData("netflixo", false)]
    [InlineData("padaria exemplo", false)]
    public void IsSubscription_ComparesWholeWords(string key, bool expected)
        => Assert.Equal(expected, MerchantHints.IsSubscription(key));

    [Theory]
    [InlineData("conta de luz", true)]
    [InlineData("vivo fibra", true)]
    [InlineData("luzia modas", false)]
    [InlineData("timao lanches", false)]
    public void IsUtility_ComparesWholeWords(string key, bool expected)
        => Assert.Equal(expected, MerchantHints.IsUtility(key));

    // ---------------------------------------------------------------- transfer to a person (3.8)

    [Theory]
    [InlineData("Pix enviado João", null)]
    [InlineData("JOAO", "TED")]
    [InlineData("MARIA S SILVA", null)]
    [InlineData("Maria da Silva", null)]
    [InlineData("Loja Exemplo", "Pix enviado")]
    [InlineData("Transf. recebida", null)]
    [InlineData("DOC 123", null)]
    [InlineData("Presente para Ana", null)]
    [InlineData("TRANSFERÊNCIA", null)]
    public void IsPersonTransfer_True(string merchant, string? description)
        => Assert.True(PersonTransferRule.IsPersonTransfer(merchant, description));

    [Theory]
    [InlineData("Padaria Silva", null)]
    [InlineData("Doceria Exemplo", null)]
    [InlineData("Teddy Brinquedos", null)]
    [InlineData("Pixel Games", null)]
    [InlineData("Mercado Exemplo", "Compras da semana")]
    [InlineData("S A", null)]
    [InlineData("Para", null)]
    public void IsPersonTransfer_False(string merchant, string? description)
        => Assert.False(PersonTransferRule.IsPersonTransfer(merchant, description));

    /// <summary>Statement lines have no merchant: the description is the establishment, and a name alone is a person.</summary>
    [Fact]
    public void IsPersonTransfer_ReadsTheDescriptionAsTheEstablishment_WhenThereIsNoMerchant()
    {
        Assert.True(PersonTransferRule.IsPersonTransfer(null, "Maria S Silva", "Maria S Silva"));
        Assert.False(PersonTransferRule.IsPersonTransfer(null, "Padaria Silva", "Padaria Silva"));
    }

    [Fact]
    public void TheTransferMarkers_AreTheLockedList()
        => Assert.Equal(["pix", "ted", "doc", "transferencia", "transf", "enviado", "recebido", "para"], PersonTransferRule.Markers);

    [Fact]
    public void TheCommonPersonNames_AreLocked()
    {
        Assert.Equal(187, PersonTransferRule.CommonPersonNames.Count);
        Assert.Equal(PersonTransferRule.CommonPersonNames.Count, PersonTransferRule.CommonPersonNames.Distinct().Count());
        Assert.All(PersonTransferRule.CommonPersonNames, n => Assert.Equal(MerchantKey.Fold(n), n));
        Assert.Contains("maria", PersonTransferRule.CommonPersonNames);
        Assert.Contains("silva", PersonTransferRule.CommonPersonNames);
        Assert.Equal("maria", PersonTransferRule.CommonPersonNames[0]);
        Assert.Equal("macedo", PersonTransferRule.CommonPersonNames[^1]);
    }

    private static string FindDirectory(params string[] relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. relativePath]);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(Path.Combine(relativePath) + " not found above " + AppContext.BaseDirectory);
    }
}
