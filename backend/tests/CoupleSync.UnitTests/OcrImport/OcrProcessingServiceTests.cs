using System.Text.Json;
using CoupleSync.Application.OcrImport;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.OcrImport;

public sealed class OcrProcessingServiceTests
{
    private static readonly Guid CoupleId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ─── SanitizeDescription ────────────────────────────────────────────────

    [Fact]
    public void SanitizeDescription_NullOrWhiteSpace_ReturnsDefault()
    {
        Assert.Equal("Transação importada", OcrProcessingService.SanitizeDescription(""));
        Assert.Equal("Transação importada", OcrProcessingService.SanitizeDescription("   "));
    }

    [Fact]
    public void SanitizeDescription_RemovesControlCharacters()
    {
        var input = "Mercado\x00Super\x1FStore";
        var result = OcrProcessingService.SanitizeDescription(input);
        Assert.Equal("MercadoSuperStore", result);
    }

    [Fact]
    public void SanitizeDescription_CollapsesWhitespace()
    {
        var result = OcrProcessingService.SanitizeDescription("  Padaria   Real  ");
        Assert.Equal("Padaria Real", result);
    }

    [Fact]
    public void SanitizeDescription_TruncatesAt200Characters()
    {
        var longInput = new string('A', 250);
        var result = OcrProcessingService.SanitizeDescription(longInput);
        Assert.Equal(200, result.Length);
    }

    // ─── ComputeFingerprint ──────────────────────────────────────────────────

    [Fact]
    public void ComputeFingerprint_SameInputs_ProduceSameFingerprint()
    {
        var date = new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var fp1 = OcrProcessingService.ComputeFingerprint(CoupleId, date, 99.90m, "Supermercado");
        var fp2 = OcrProcessingService.ComputeFingerprint(CoupleId, date, 99.90m, "supermercado");
        Assert.Equal(fp1, fp2); // normalized to lowercase
    }

    [Fact]
    public void ComputeFingerprint_DifferentAmounts_ProduceDifferentFingerprints()
    {
        var date = new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var fp1 = OcrProcessingService.ComputeFingerprint(CoupleId, date, 99.90m, "Supermercado");
        var fp2 = OcrProcessingService.ComputeFingerprint(CoupleId, date, 100.00m, "Supermercado");
        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void ComputeFingerprint_ReturnsLowercaseHex64Chars()
    {
        var date = new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var fp = OcrProcessingService.ComputeFingerprint(CoupleId, date, 10m, "desc");
        Assert.Equal(64, fp.Length);
        Assert.Equal(fp, fp.ToLowerInvariant());
    }

    // ─── ParseCandidates ─────────────────────────────────────────────────────

    [Fact]
    public void ParseCandidates_ValidReceiptJson_ReturnsCandidates()
    {
        var json = BuildReceiptJson(new[]
        {
            new { Description = "Frango Assado", Amount = 29.90m, Currency = "BRL" },
            new { Description = "Suco de Uva",   Amount = 8.50m,  Currency = "BRL" },
        }, transactionDate: "2025-03-15");

        var candidates = OcrProcessingService.ParseCandidates(json);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(0, candidates[0].Index);
        Assert.Equal("Frango Assado", candidates[0].Description);
        Assert.Equal(29.90m, candidates[0].Amount);
        Assert.Equal("BRL", candidates[0].Currency);
        Assert.Equal(new DateTime(2025, 3, 15, 0, 0, 0, DateTimeKind.Utc), candidates[0].Date);
        Assert.Equal(1, candidates[1].Index);
    }

    [Fact]
    public void ParseCandidates_MissingAnalyzeResult_ReturnsEmpty()
    {
        var candidates = OcrProcessingService.ParseCandidates("{}");
        Assert.Empty(candidates);
    }

    [Fact]
    public void ParseCandidates_NoItems_ReturnsEmpty()
    {
        var json = """{"analyzeResult":{"documents":[{"fields":{}}]}}""";
        var candidates = OcrProcessingService.ParseCandidates(json);
        Assert.Empty(candidates);
    }

    // ─── ParseAndDeduplicateAsync ────────────────────────────────────────────

    [Fact]
    public async Task ParseAndDeduplicateAsync_NoDuplicates_AllFlagsAreFalse()
    {
        var repo = new FakeTransactionRepository(existingFingerprints: []);
        var service = new OcrProcessingService(repo, new NullCategoryClassifier(), new FakeBudgetRepository());

        var json = BuildReceiptJson(new[]
        {
            new { Description = "Farmácia", Amount = 45.00m, Currency = "BRL" },
        }, transactionDate: "2025-06-01");

        var result = await service.ParseAndDeduplicateAsync(CoupleId, json, CancellationToken.None);

        Assert.Single(result);
        Assert.False(result[0].DuplicateSuspected);
    }

    [Fact]
    public async Task ParseAndDeduplicateAsync_DuplicateExists_FlagIsTrue()
    {
        // Pre-compute the expected fingerprint
        var date = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        // SanitizeDescription and Math.Abs are applied before fingerprint, so compute after sanitize
        var sanitized = OcrProcessingService.SanitizeDescription("Farmácia");
        var expectedFp = OcrProcessingService.ComputeFingerprint(CoupleId, date, 45.00m, sanitized);

        var repo = new FakeTransactionRepository(existingFingerprints: [expectedFp]);
        var service = new OcrProcessingService(repo, new NullCategoryClassifier(), new FakeBudgetRepository());

        var json = BuildReceiptJson(new[]
        {
            new { Description = "Farmácia", Amount = 45.00m, Currency = "BRL" },
        }, transactionDate: "2025-06-01");

        var result = await service.ParseAndDeduplicateAsync(CoupleId, json, CancellationToken.None);

        Assert.Single(result);
        Assert.True(result[0].DuplicateSuspected);
    }

    [Fact]
    public async Task ParseAndDeduplicateAsync_NegativeAmount_NormalizedToPositive()
    {
        var repo = new FakeTransactionRepository(existingFingerprints: []);
        var service = new OcrProcessingService(repo, new NullCategoryClassifier(), new FakeBudgetRepository());

        var json = BuildReceiptJson(new[]
        {
            new { Description = "Estorno", Amount = -20.00m, Currency = "BRL" },
        }, transactionDate: "2025-06-01");

        var result = await service.ParseAndDeduplicateAsync(CoupleId, json, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(20.00m, result[0].Amount);
    }

    // ─── ParseCandidates — local-pdf discriminator ───────────────────────────

    [Fact]
    public void ParseCandidates_LocalPdfJson_ReturnsCorrectCandidates()
    {
        var json = """
            {
              "provider": "local-pdf",
              "bankName": "Nubank",
              "transactions": [
                { "date": "2026-01-15", "description": "PIX Recebido", "amount": 1500.00, "type": "Credit" },
                { "date": "2026-01-16", "description": "Mercado Livre", "amount": 89.90, "type": "Debit" }
              ]
            }
            """;

        var candidates = OcrProcessingService.ParseCandidates(json);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(0, candidates[0].Index);
        Assert.Equal("PIX Recebido", candidates[0].Description);
        Assert.Equal(1500.00m, candidates[0].Amount);
        Assert.Equal("BRL", candidates[0].Currency);
        Assert.Equal(1.0, candidates[0].Confidence);
        Assert.Equal(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), candidates[0].Date);

        Assert.Equal(1, candidates[1].Index);
        Assert.Equal("Mercado Livre", candidates[1].Description);
        Assert.Equal(89.90m, candidates[1].Amount);
    }

    [Fact]
    public void ParseCandidates_LocalPdfJson_EmptyTransactions_ReturnsEmpty()
    {
        var json = """{"provider":"local-pdf","bankName":"Inter","transactions":[]}""";

        var candidates = OcrProcessingService.ParseCandidates(json);

        Assert.Empty(candidates);
    }

    [Fact]
    public void ParseCandidates_LocalPdfJson_MissingTransactionsProperty_ReturnsEmpty()
    {
        var json = """{"provider":"local-pdf","bankName":"Inter"}""";

        var candidates = OcrProcessingService.ParseCandidates(json);

        Assert.Empty(candidates);
    }

    [Fact]
    public void ParseCandidates_LocalPdfJson_DoesNotRouteToAzureBranch()
    {
        // Ensure local-pdf JSON does not try to parse analyzeResult and silently return empty
        var json = """
            {
              "provider": "local-pdf",
              "bankName": "Itaú",
              "transactions": [
                { "date": "2026-02-01", "description": "Pagamento recebido", "amount": 500.00, "type": "Credit" }
              ]
            }
            """;

        var candidates = OcrProcessingService.ParseCandidates(json);

        Assert.Single(candidates);
    }

    [Fact]
    public void ParseCandidates_AzureJson_StillWorksAfterDiscriminatorChange()
    {
        // Regression: Azure format must still parse correctly after the discriminator branch was added
        var json = BuildReceiptJson(new[]
        {
            new { Description = "Café da manhã", Amount = 12.50m, Currency = "BRL" },
        }, transactionDate: "2026-03-10");

        var candidates = OcrProcessingService.ParseCandidates(json);

        Assert.Single(candidates);
        Assert.Equal("Café da manhã", candidates[0].Description);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string BuildReceiptJson(
        IEnumerable<dynamic> items,
        string transactionDate)
    {
        var itemsArray = items.Select(i => new
        {
            valueObject = new
            {
                Description = new { valueString = (string)i.Description, confidence = 0.95 },
                TotalPrice = new
                {
                    valueCurrency = new
                    {
                        amount = (decimal)i.Amount,
                        currencyCode = (string)i.Currency
                    }
                }
            }
        });

        var doc = new
        {
            analyzeResult = new
            {
                documents = new[]
                {
                    new
                    {
                        fields = new
                        {
                            TransactionDate = new { valueDate = transactionDate },
                            Items = new { valueArray = itemsArray }
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(doc);
    }

    private sealed class FakeTransactionRepository : CoupleSync.Application.Common.Interfaces.ITransactionRepository
    {
        private readonly HashSet<string> _fingerprints;

        public FakeTransactionRepository(IEnumerable<string> existingFingerprints)
        {
            _fingerprints = [.. existingFingerprints];
        }

        public Task<bool> FingerprintExistsAsync(string fingerprint, Guid coupleId, CancellationToken ct) =>
            Task.FromResult(_fingerprints.Contains(fingerprint));

        public Task AddTransactionAsync(CoupleSync.Domain.Entities.Transaction transaction, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<(int TotalCount, IReadOnlyList<CoupleSync.Domain.Entities.Transaction> Items)> GetPagedAsync(
            Guid coupleId, int page, int pageSize, string? category,
            DateTime? startDate, DateTime? endDate, CancellationToken ct) =>
            Task.FromResult<(int, IReadOnlyList<CoupleSync.Domain.Entities.Transaction>)>((0, []));

        public Task<CoupleSync.Domain.Entities.Transaction?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct) =>
            Task.FromResult<CoupleSync.Domain.Entities.Transaction?>(null);

        public Task<IReadOnlyList<CoupleSync.Domain.Entities.Transaction>> GetByGoalIdAsync(Guid goalId, Guid coupleId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CoupleSync.Domain.Entities.Transaction>>([]);

        public Task<IReadOnlyList<CoupleSync.Domain.Entities.Transaction>> GetRecentByCoupleAsync(Guid coupleId, DateTime since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CoupleSync.Domain.Entities.Transaction>>([]);

        public Task UpdateAsync(CoupleSync.Domain.Entities.Transaction transaction, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<Dictionary<string, decimal>> GetActualSpentByCategoryAsync(Guid coupleId, DateTime startUtc, DateTime endUtc, CancellationToken ct) =>
            Task.FromResult(new Dictionary<string, decimal>());

        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
