using CoupleSync.Application.Ai;
using CoupleSync.Application.AiFacts;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.AiFacts;

/// <summary>
/// Issue #39, review round 4 — no text of a transaction stops the detection (I2), and the day of a charge and
/// "today" are the days of Brasília, never the UTC ones (I3). Establishments and numbers are made up.
/// </summary>
public sealed class RecurrenceDetectorReview4Tests
{
    private static readonly Guid Ana = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly RecurrenceOptions Options = new();

    private static RecurrenceRow At(DateTime utc, string? merchant, decimal amount, string? description = null, string category = "LAZER")
        => new(Guid.NewGuid(), DateTime.SpecifyKind(utc, DateTimeKind.Utc), amount, merchant, description, category, Ana, TransactionSource.Notification);

    // ---------------------------------------------------------------- I2: digits that are not 0 to 9

    /// <summary>
    /// "1/3" written with full-width or Arabic-Indic digits, in the establishment or in the description, is not the
    /// mark of an instalment and does not stop the detection: what the group pays is found all the same.
    /// </summary>
    [Theory]
    [InlineData("１/３")]
    [InlineData("١/٣")]
    [InlineData("1/３")]
    [InlineData("١٢/١٢")]
    public void AMarkWrittenWithDigitsOfAnotherScript_IsNoInstalment_AndDoesNotStopTheDetection(string mark)
    {
        var today = new DateOnly(2026, 10, 20);
        var rows = new List<RecurrenceRow>
        {
            At(new DateTime(2026, 8, 5, 15, 0, 0), "Streaming Exemplo", 39.90m),
            At(new DateTime(2026, 9, 5, 15, 0, 0), "Streaming Exemplo", 39.90m),
            At(new DateTime(2026, 10, 5, 15, 0, 0), "Streaming Exemplo", 39.90m),
            At(new DateTime(2026, 10, 12, 15, 0, 0), $"Loja Exemplo {mark}", 90m, category: "COMPRAS"),
            At(new DateTime(2026, 10, 13, 15, 0, 0), null, 70m, description: $"compra {mark}", category: "COMPRAS"),
        };

        var streams = RecurrenceDetector.Detect(rows, today, Options).Streams;

        var stream = Assert.Single(streams);
        Assert.Equal("streaming exemplo", stream.MerchantKey);
        Assert.Equal(RecurringKinds.Subscription, stream.Facts.Kind);
    }

    /// <summary>The same two purchases with the mark in ASCII are what they were: a probable instalment each.</summary>
    [Fact]
    public void TheSameMarkInAscii_IsStillAProbableInstalment()
    {
        var today = new DateOnly(2026, 10, 20);
        var rows = new List<RecurrenceRow>
        {
            At(new DateTime(2026, 10, 12, 15, 0, 0), "Loja Exemplo 1/3", 90m, category: "COMPRAS"),
            At(new DateTime(2026, 10, 13, 15, 0, 0), null, 70m, description: "compra 1/3", category: "COMPRAS"),
        };

        var streams = RecurrenceDetector.Detect(rows, today, Options).Streams;

        Assert.Equal(2, streams.Count);
        Assert.All(streams, s => Assert.Equal(RecurringKinds.Installment, s.Facts.Kind));
        Assert.All(streams, s => Assert.Equal((1, 3), (s.Facts.InstallmentNumber, s.Facts.InstallmentTotal)));
    }

    /// <summary>
    /// The check digits of a CPF or of a CNPJ are only counted over 0 to 9. A run with a digit of another script is
    /// not "a valid document" by an account made with the code of the character (with that account, the two runs
    /// below "had" the check digits of a CPF).
    /// </summary>
    [Theory]
    [InlineData("ref.１2345678968")]
    [InlineData("ref.١2345678909")]
    public void ARunWithADigitOfAnotherScript_IsNotAValidDocumentByItsCheckDigits(string text)
        => Assert.False(FactPackPrivacyFilter.HasDocumentOrContact(text));

    /// <summary>What was a document goes on being one (the classic example numbers).</summary>
    [Theory]
    [InlineData("ref.52998224725")]
    [InlineData("ref.11222333000181")]
    [InlineData("529.982.247-25")]
    public void AValidDocumentInAscii_IsStillADocument(string text)
        => Assert.True(FactPackPrivacyFilter.HasDocumentOrContact(text));

    // ---------------------------------------------------------------- I3: the day is the one of Brasília

    /// <summary>
    /// A charge at 02:00 UTC of the 1st is of 23:00 of the last day of the month before, in Brasília: the dates of
    /// the stream are 31/07, 31/08 and 30/09, and the next one is expected a month after the last.
    /// </summary>
    [Fact]
    public void AChargeAtTwoInTheMorningUtcOfTheFirst_IsOfTheLastDayOfTheMonthBefore()
    {
        var rows = new List<RecurrenceRow>
        {
            At(new DateTime(2026, 8, 1, 2, 0, 0), "Streaming Exemplo", 39.90m),
            At(new DateTime(2026, 9, 1, 2, 0, 0), "Streaming Exemplo", 39.90m),
            At(new DateTime(2026, 10, 1, 2, 0, 0), "Streaming Exemplo", 39.90m),
        };
        Assert.Equal([new DateOnly(2026, 7, 31), new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 30)], rows.Select(r => r.LocalDate));

        var result = RecurrenceDetector.Detect(rows, new DateOnly(2026, 10, 20), Options);

        var stream = Assert.Single(result.Streams);
        Assert.Equal(new DateOnly(2026, 7, 31), stream.Facts.FirstSeenLocal);
        Assert.Equal(new DateOnly(2026, 9, 30), stream.Facts.LastSeenLocal);
        Assert.Equal(new DateOnly(2026, 10, 30), stream.Facts.NextExpectedLocal);
        Assert.Equal(
            [new DateOnly(2026, 7, 31), new DateOnly(2026, 8, 31), new DateOnly(2026, 9, 30)],
            result.ChargesByKey["streaming exemplo"].Select(c => c.LocalDate).Order());
    }

    /// <summary>
    /// A charge at 01:00 UTC of the 21st was made at 22:00 of the 20th in Brasília: with "today" the 20th it is a
    /// charge of today, and counts (by the UTC day it would be of tomorrow, and left out).
    /// </summary>
    [Fact]
    public void AChargeOfTonightInBrasilia_ThatIsAlreadyTomorrowInUtc_Counts()
    {
        var rows = new List<RecurrenceRow>
        {
            At(new DateTime(2026, 8, 21, 1, 0, 0), "Streaming Exemplo", 39.90m),
            At(new DateTime(2026, 9, 21, 1, 0, 0), "Streaming Exemplo", 39.90m),
            At(new DateTime(2026, 10, 21, 1, 0, 0), "Streaming Exemplo", 39.90m),
        };

        var stream = Assert.Single(RecurrenceDetector.Detect(rows, new DateOnly(2026, 10, 20), Options).Streams);

        Assert.Equal(3, stream.Facts.Occurrences);
        Assert.Equal(new DateOnly(2026, 10, 20), stream.Facts.LastSeenLocal);
    }
}
