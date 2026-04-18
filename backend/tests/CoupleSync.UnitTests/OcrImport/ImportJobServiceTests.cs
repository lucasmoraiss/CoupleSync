using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "Ocr")]
public sealed class ImportJobServiceTests
{
    private static readonly Guid CoupleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId   = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly DateTime FixedNow = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    private static ImportJobService BuildService(
        out FakeImportJobRepository jobRepo,
        out FakeTransactionRepository txnRepo,
        FakeStorageAdapter? storage = null)
    {
        jobRepo = new FakeImportJobRepository();
        txnRepo = new FakeTransactionRepository();
        storage ??= new FakeStorageAdapter();
        var dateTime = new FakeDateTimeProvider(FixedNow);
        return new ImportJobService(jobRepo, storage, dateTime, txnRepo);
    }

    // ── UploadAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadAsync_ValidFile_CreatesJobAndReturnsId()
    {
        var svc = BuildService(out var repo, out _);

        var stream = new MemoryStream(JpegBytes);
        var id = await svc.UploadAsync(CoupleId, UserId, stream, "image/jpeg", CancellationToken.None);

        Assert.NotEqual(Guid.Empty, id);
        Assert.Single(repo.Jobs);
        Assert.Equal(id, repo.Jobs[0].Id);
        Assert.Equal(CoupleId, repo.Jobs[0].CoupleId);
        Assert.Equal(UserId, repo.Jobs[0].UserId);
        Assert.Equal(ImportJobStatus.Pending, repo.Jobs[0].Status);
    }

    // ── GetJobAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJobAsync_ExistingJob_ReturnsJob()
    {
        var svc = BuildService(out var repo, out _);
        var stream = new MemoryStream(JpegBytes);
        var id = await svc.UploadAsync(CoupleId, UserId, stream, "image/jpeg", CancellationToken.None);

        var job = await svc.GetJobAsync(id, CoupleId, CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(id, job!.Id);
    }

    [Fact]
    public async Task GetJobAsync_NonExistent_ReturnsNull()
    {
        var svc = BuildService(out _, out _);

        var job = await svc.GetJobAsync(Guid.NewGuid(), CoupleId, CancellationToken.None);

        Assert.Null(job);
    }

    // ── GetCandidatesAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetCandidatesAsync_ReadyJob_ReturnsCandidates()
    {
        var svc = BuildService(out var repo, out _);
        var stream = new MemoryStream(JpegBytes);
        var id = await svc.UploadAsync(CoupleId, UserId, stream, "image/jpeg", CancellationToken.None);

        var job = repo.Jobs[0];
        job.MarkProcessing(FixedNow);
        job.MarkReady(BuildCandidatesJson(2), FixedNow);

        var candidates = await svc.GetCandidatesAsync(id, CoupleId, CancellationToken.None);

        Assert.NotNull(candidates);
        Assert.Equal(2, candidates!.Count);
    }

    [Fact]
    public async Task GetCandidatesAsync_PendingJob_ThrowsConflict()
    {
        var svc = BuildService(out var repo, out _);
        var stream = new MemoryStream(JpegBytes);
        var id = await svc.UploadAsync(CoupleId, UserId, stream, "image/jpeg", CancellationToken.None);
        // Job stays Pending (not calling MarkProcessing/MarkReady)

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => svc.GetCandidatesAsync(id, CoupleId, CancellationToken.None));

        Assert.Equal("OCR_JOB_NOT_READY", ex.Code);
    }

    [Fact]
    public async Task GetCandidatesAsync_ConfirmedJob_ThrowsConflictWithAlreadyConfirmed()
    {
        var svc = BuildService(out var repo, out _);
        var stream = new MemoryStream(JpegBytes);
        var id = await svc.UploadAsync(CoupleId, UserId, stream, "image/jpeg", CancellationToken.None);

        var job = repo.Jobs[0];
        job.MarkProcessing(FixedNow);
        job.MarkReady(BuildCandidatesJson(1), FixedNow);
        job.MarkConfirmed(FixedNow);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => svc.GetCandidatesAsync(id, CoupleId, CancellationToken.None));

        Assert.Equal("OCR_JOB_ALREADY_CONFIRMED", ex.Code);
    }

    [Fact]
    public async Task GetCandidatesAsync_NonExistent_ReturnsNull()
    {
        var svc = BuildService(out _, out _);

        var candidates = await svc.GetCandidatesAsync(Guid.NewGuid(), CoupleId, CancellationToken.None);

        Assert.Null(candidates);
    }

    // ── ConfirmCandidatesAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ConfirmCandidatesAsync_ValidSelection_CreatesTransactions()
    {
        var svc = BuildService(out var repo, out var txnRepo);
        var stream = new MemoryStream(JpegBytes);
        var id = await svc.UploadAsync(CoupleId, UserId, stream, "image/jpeg", CancellationToken.None);

        var job = repo.Jobs[0];
        job.MarkProcessing(FixedNow);
        job.MarkReady(BuildCandidatesJson(3), FixedNow);

        var created = await svc.ConfirmCandidatesAsync(id, CoupleId, UserId, [0, 2], null, CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(2, created!.Count);
        Assert.Equal(2, txnRepo.AddedTransactions.Count);
        Assert.All(txnRepo.AddedTransactions, t =>
        {
            Assert.Equal(CoupleId, t.CoupleId);
            Assert.Equal(UserId, t.UserId);
        });
    }

    [Fact]
    public async Task ConfirmCandidatesAsync_TransitionsJobToConfirmed()
    {
        var svc = BuildService(out var repo, out _);
        var stream = new MemoryStream(JpegBytes);
        var id = await svc.UploadAsync(CoupleId, UserId, stream, "image/jpeg", CancellationToken.None);

        var job = repo.Jobs[0];
        job.MarkProcessing(FixedNow);
        job.MarkReady(BuildCandidatesJson(2), FixedNow);

        await svc.ConfirmCandidatesAsync(id, CoupleId, UserId, [0], null, CancellationToken.None);

        Assert.Equal(ImportJobStatus.Confirmed, repo.Jobs[0].Status);
    }

    [Fact]
    public async Task ConfirmCandidatesAsync_NullIndices_ThrowsUnprocessable()
    {
        var svc = BuildService(out _, out _);

        await Assert.ThrowsAsync<UnprocessableEntityException>(
            () => svc.ConfirmCandidatesAsync(Guid.NewGuid(), CoupleId, UserId, null!, null, CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmCandidatesAsync_EmptyIndices_ThrowsUnprocessable()
    {
        var svc = BuildService(out _, out _);

        await Assert.ThrowsAsync<UnprocessableEntityException>(
            () => svc.ConfirmCandidatesAsync(Guid.NewGuid(), CoupleId, UserId, [], null, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "AutoCategorization")]
    public async Task ConfirmCandidatesAsync_UsesSuggestedCategory_WhenCandidateHasSuggestion()
    {
        var svc = BuildService(out var repo, out var txnRepo);
        var stream = new MemoryStream(JpegBytes);
        var id = await svc.UploadAsync(CoupleId, UserId, stream, "image/jpeg", CancellationToken.None);

        var job = repo.Jobs[0];
        job.MarkProcessing(FixedNow);
        job.MarkReady(BuildCandidatesJson(2, suggestedCategory: "Alimentação"), FixedNow);

        var created = await svc.ConfirmCandidatesAsync(id, CoupleId, UserId, [0, 1], null, CancellationToken.None);

        Assert.NotNull(created);
        Assert.All(created!, t => Assert.Equal("Alimentação", t.Category));
    }

    [Fact]
    [Trait("Category", "AutoCategorization")]
    public async Task ConfirmCandidatesAsync_FallsBackToOutros_WhenSuggestedCategoryIsNull()
    {
        var svc = BuildService(out var repo, out var txnRepo);
        var stream = new MemoryStream(JpegBytes);
        var id = await svc.UploadAsync(CoupleId, UserId, stream, "image/jpeg", CancellationToken.None);

        var job = repo.Jobs[0];
        job.MarkProcessing(FixedNow);
        job.MarkReady(BuildCandidatesJson(2, suggestedCategory: null), FixedNow);

        var created = await svc.ConfirmCandidatesAsync(id, CoupleId, UserId, [0, 1], null, CancellationToken.None);

        Assert.NotNull(created);
        Assert.All(created!, t => Assert.Equal("Outros", t.Category));
    }

    [Fact]
    [Trait("Category", "AutoCategorization")]
    public async Task ConfirmCandidatesAsync_CategoryOverrideTakesPrecedence_OverSuggestedCategory()
    {
        var svc = BuildService(out var repo, out var txnRepo);
        var stream = new MemoryStream(JpegBytes);
        var id = await svc.UploadAsync(CoupleId, UserId, stream, "image/jpeg", CancellationToken.None);

        var job = repo.Jobs[0];
        job.MarkProcessing(FixedNow);
        job.MarkReady(BuildCandidatesJson(2, suggestedCategory: "Alimentação"), FixedNow);

        var overrides = new Dictionary<int, string> { { 0, "Saúde" }, { 1, "Transporte" } };
        var created = await svc.ConfirmCandidatesAsync(id, CoupleId, UserId, [0, 1], overrides, CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal("Saúde", created![0].Category);
        Assert.Equal("Transporte", created![1].Category);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string BuildCandidatesJson(int count, string? suggestedCategory = null)
    {
        var candidates = Enumerable.Range(0, count)
            .Select(i => new OcrCandidate
            {
                Index            = i,
                Date             = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
                Description      = $"Item {i + 1}",
                Amount           = 10m * (i + 1),
                Currency         = "BRL",
                Confidence       = 0.95,
                Fingerprint      = $"fp{i:D4}",
                SuggestedCategory = suggestedCategory
            })
            .ToList();

        return JsonSerializer.Serialize(candidates);
    }
}

// ── Fakes ──────────────────────────────────────────────────────────────────

internal sealed class FakeStorageAdapter : IStorageAdapter
{
    public Task<string> UploadAsync(Guid coupleId, Guid uploadId, Stream content, string mimeType, CancellationToken ct)
        => Task.FromResult($"couples/{coupleId}/{uploadId}");

    public Task<Stream> DownloadAsync(string storagePath, CancellationToken ct)
        => Task.FromResult<Stream>(new MemoryStream());
}

internal sealed class FakeOcrProvider : IOcrProvider
{
    public string ResponseJson { get; set; } = """{"analyzeResult":{"documents":[]}}""";

    public Task<string> AnalyzeAsync(string storagePath, string mimeType, CancellationToken ct)
        => Task.FromResult(ResponseJson);
}

internal sealed class FakeImportJobRepository : IImportJobRepository
{
    public List<ImportJob> Jobs { get; } = new();

    public Task<ImportJob?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
    {
        var job = Jobs.FirstOrDefault(j => j.Id == id && j.CoupleId == coupleId);
        return Task.FromResult<ImportJob?>(job);
    }

    public Task AddAsync(ImportJob job, CancellationToken ct)
    {
        Jobs.Add(job);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<ImportJob>> GetPendingAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ImportJob>>(Jobs.Where(j => j.Status == ImportJobStatus.Pending).Take(limit).ToList());
}

internal sealed class FakeDateTimeProvider : IDateTimeProvider
{
    public FakeDateTimeProvider(DateTime fixedNow) => UtcNow = fixedNow;
    public DateTime UtcNow { get; }
}

internal sealed class FakeTransactionRepository : ITransactionRepository
{
    public List<Transaction> AddedTransactions { get; } = new();

    public Task<bool> FingerprintExistsAsync(string fingerprint, Guid coupleId, CancellationToken ct)
        => Task.FromResult(false);

    public Task AddTransactionAsync(Transaction transaction, CancellationToken ct)
    {
        AddedTransactions.Add(transaction);
        return Task.CompletedTask;
    }

    public Task<(int TotalCount, IReadOnlyList<Transaction> Items)> GetPagedAsync(
        Guid coupleId, int page, int pageSize, string? category,
        DateTime? startDate, DateTime? endDate, CancellationToken ct)
        => Task.FromResult<(int, IReadOnlyList<Transaction>)>((0, []));

    public Task<Transaction?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
        => Task.FromResult<Transaction?>(null);

    public Task<IReadOnlyList<Transaction>> GetByGoalIdAsync(Guid goalId, Guid coupleId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Transaction>>([]);

    public Task<IReadOnlyList<Transaction>> GetRecentByCoupleAsync(Guid coupleId, DateTime since, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Transaction>>([]);

    public Task UpdateAsync(Transaction transaction, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<Dictionary<string, decimal>> GetActualSpentByCategoryAsync(
        Guid coupleId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
        => Task.FromResult(new Dictionary<string, decimal>());

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

//  Local classifier stubs removed — classification now lives in OcrProcessingService.
