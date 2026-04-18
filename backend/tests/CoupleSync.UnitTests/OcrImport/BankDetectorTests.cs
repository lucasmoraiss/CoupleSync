using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "BankDetection")]
public sealed class BankDetectorTests
{
    [Fact]
    public void ReturnsFirstMatchingParser_WhenMultipleParsersRegistered()
    {
        var first = new FakeParser("BancoA", canParse: true);
        var second = new FakeParser("BancoB", canParse: true);
        var detector = new BankDetector([first, second]);

        var result = detector.Detect("some bank statement text");

        Assert.Same(first, result);
    }

    [Fact]
    public void ThrowsBankFormatUnknownException_WhenNoParsersMatch()
    {
        var detector = new BankDetector([
            new FakeParser("BancoA", canParse: false),
            new FakeParser("BancoB", canParse: false)
        ]);

        var ex = Assert.Throws<BankFormatUnknownException>(() =>
            detector.Detect("unrecognized statement text"));

        Assert.Equal("BANK_FORMAT_UNKNOWN", ex.Code);
    }

    [Fact]
    public void ThrowsBankFormatUnknownException_WhenNoParsersRegistered()
    {
        var detector = new BankDetector([]);

        Assert.Throws<BankFormatUnknownException>(() => detector.Detect("any text"));
    }

    [Fact]
    public void SkipsNonMatchingParsers_ReturnsSecondMatchingParser()
    {
        var first = new FakeParser("BancoA", canParse: false);
        var second = new FakeParser("BancoB", canParse: true);
        var detector = new BankDetector([first, second]);

        var result = detector.Detect("some statement");

        Assert.Same(second, result);
    }

    // ── Test double ───────────────────────────────────────────────────────────

    private sealed class FakeParser : IBankStatementParser
    {
        private readonly bool _canParse;

        public FakeParser(string bankName, bool canParse)
        {
            BankName = bankName;
            _canParse = canParse;
        }

        public string BankName { get; }

        public bool CanParse(string extractedText) => _canParse;

        public IReadOnlyList<ParsedTransaction> Parse(string extractedText) =>
            Array.Empty<ParsedTransaction>();
    }
}
