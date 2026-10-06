using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Security;

namespace CoupleSync.UnitTests.Globalization;

/// <summary>
/// Results that are stored or compared must not change with the culture of the host: production ran with the
/// invariant culture (no ICU), a developer machine runs in pt-BR, and the container now ships ICU.
/// </summary>
public sealed class CultureIndependenceTests
{
    private static readonly Guid CoupleId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime When = new(2026, 10, 3, 13, 0, 0, DateTimeKind.Utc);

    private static T Under<T>(string culture, Func<T> action)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(culture);
        try { return action(); }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Theory]
    [InlineData("")]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void NotificationFingerprint_IsTheSameInEveryCulture_AndEqualsWhatProductionAlreadyStored(string culture)
    {
        var fingerprint = Under(culture, () =>
            TransactionFingerprintGenerator.GenerateStatic(CoupleId, " nubank ", 1234.5m, "brl", When, " Loja "));

        // The text production has always hashed (invariant culture): dot as decimal separator.
        Assert.Equal(Sha256($"{CoupleId}|NUBANK|1234.50|BRL|2026-10-03T13:00:00.0000000Z|LOJA"), fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void StatementLineFingerprint_IsTheSameInEveryCulture_AndEqualsWhatProductionAlreadyStored(string culture)
    {
        var fingerprint = Under(culture, () =>
            OcrProcessingService.ComputeFingerprint(CoupleId, When, 1234.5m, " Mercado "));

        Assert.Equal(Sha256($"{CoupleId}|2026-10-03|1234.50|mercado"), fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void StatementDates_AreReadTheSameWayInEveryCulture(string culture)
    {
        const string json = """{"provider":"local-pdf","bankName":"Inter","transactions":[{"date":"2026-03-04","description":"a","amount":1,"type":"Debit"},{"date":"03/04/2026","description":"b","amount":1,"type":"Debit"}]}""";

        var candidates = Under(culture, () => OcrProcessingService.ParseCandidates(json));

        Assert.Equal(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc), candidates[0].Date);
        Assert.Equal(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc), candidates[1].Date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    public void BrlFormat_IsTheSameInEveryCulture(string culture)
        => Assert.Equal("R$ 1.234,50", Under(culture, () => BrlFormat.Format(1234.5m)));

    [Theory]
    [InlineData("")]
    [InlineData("pt-BR")]
    [InlineData("tr-TR")]   // the culture where "i".ToUpper() is not "I"
    public void CategoryMatching_IsTheSameInEveryCulture(string culture)
    {
        Assert.Equal("ALIMENTACAO", Under(culture, () => TransactionCategories.TryNormalize("Alimentação")));
        Assert.Equal("MORADIA", Under(culture, () => TransactionCategories.TryNormalize("moradia")));
        Assert.Equal("SAUDE", Under(culture, () => TransactionCategories.TryNormalize("Saúde")));
    }

    [Fact]
    public void AccentFolding_CoversEveryAccentedLetterOfPortuguese_InBothCases()
    {
        Assert.Equal("aaaaeeiooouc AAAAEEIOOOUC", AccentFolding.RemoveAccents("áàâãéêíóôõúç ÁÀÂÃÉÊÍÓÔÕÚÇ"));
        // Decomposed text (letter + combining mark) loses the marks.
        Assert.Equal("cao", AccentFolding.RemoveAccents("ção"));
        Assert.Equal("plain text 123", AccentFolding.RemoveAccents("plain text 123"));
    }
}
