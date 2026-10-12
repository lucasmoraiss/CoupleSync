using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>Issue 56: a group cannot have more than N import jobs unfinished at once.</summary>
[Trait("Category", "Ocr")]
public sealed class ImportQueueLimitTests
{
    private static readonly Guid CoupleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherCoupleId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly byte[] PdfBytes = "%PDF-1.4"u8.ToArray();
    private static readonly DateTime FixedNow = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    private static ImportJobService Build(FakeImportJobRepository jobs, FakeStorageAdapter storage, int? limit = null)
        => new(jobs, storage, new FakeDateTimeProvider(FixedNow), new FakeTransactionRepository(),
            new FakeNotificationCaptureRepository(), new FakeAlertPolicyService(), new FakeNotificationEventRepository(),
            NullLogger<ImportJobService>.Instance,
            limit is null ? null : Options.Create(new ImportQueueOptions { MaxUnfinishedJobsPerCouple = limit.Value }));

    private static ImportJob Job(Guid coupleId) =>
        ImportJob.Create(coupleId, UserId, "uploads/x.pdf", "application/pdf", FixedNow);

    private static Task<Guid> Upload(ImportJobService svc, Guid? coupleId = null)
        => svc.UploadAsync(coupleId ?? CoupleId, UserId, new MemoryStream(PdfBytes), "application/pdf", CancellationToken.None);

    [Fact]
    public async Task TheDefaultLimitIsThree_TheFourthUnfinishedJobIsRefusedInPortuguese_BeforeTheFileIsStored()
    {
        var jobs = new FakeImportJobRepository();
        var storage = new FakeStorageAdapter();
        var svc = Build(jobs, storage);
        for (var i = 0; i < 3; i++) await Upload(svc);
        var storedBefore = storage.UploadCount;

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Upload(svc));

        Assert.Equal("IMPORT_QUEUE_FULL", ex.Code);
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("Já há 3 extratos sendo lidos. Espere um deles terminar para enviar outro.", ex.Message);
        Assert.Equal(storedBefore, storage.UploadCount);
        Assert.Equal(3, jobs.Jobs.Count);
    }

    [Fact]
    public async Task AJobThatEnded_FreesASlot()
    {
        var jobs = new FakeImportJobRepository();
        var svc = Build(jobs, new FakeStorageAdapter());
        for (var i = 0; i < 3; i++) await Upload(svc);
        jobs.Jobs[0].MarkFailed("PDF_TIMEOUT", "x", FixedNow);

        await Upload(svc);

        Assert.Equal(4, jobs.Jobs.Count);
    }

    [Fact]
    public async Task OnlyPendingAndProcessingCount_ReadyAndFailedDoNot()
    {
        var jobs = new FakeImportJobRepository();
        var processing = Job(CoupleId); processing.MarkProcessing(FixedNow);
        var ready = Job(CoupleId); ready.MarkProcessing(FixedNow); ready.MarkReady("[]", FixedNow);
        var failed = Job(CoupleId); failed.MarkFailed("PDF_TIMEOUT", "x", FixedNow);
        jobs.Jobs.AddRange([processing, ready, failed, Job(CoupleId)]);
        var svc = Build(jobs, new FakeStorageAdapter());

        await Upload(svc); // 2 unfinished: room for the third
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Upload(svc));

        Assert.Equal("IMPORT_QUEUE_FULL", ex.Code);
    }

    [Fact]
    public async Task AnotherGroupIsNotAffected()
    {
        var jobs = new FakeImportJobRepository();
        var svc = Build(jobs, new FakeStorageAdapter());
        for (var i = 0; i < 3; i++) await Upload(svc);

        await Upload(svc, OtherCoupleId);

        Assert.Single(jobs.Jobs, j => j.CoupleId == OtherCoupleId);
    }

    [Fact]
    public async Task AnAbandonedProcessingJob_IsNotCounted_SoTheGroupIsNeverLockedOut()
    {
        var jobs = new FakeImportJobRepository();
        for (var i = 0; i < 3; i++)
        {
            var abandoned = Job(CoupleId);
            abandoned.MarkProcessing(FixedNow - ImportJobRecovery.ProcessingTimeout - TimeSpan.FromMinutes(1));
            jobs.Jobs.Add(abandoned);
        }
        var svc = Build(jobs, new FakeStorageAdapter());

        await Upload(svc);

        Assert.Equal(4, jobs.Jobs.Count);
    }

    [Theory]
    [InlineData(1, "Já há 1 extrato sendo lido. Espere ele terminar para enviar outro.")]
    [InlineData(5, "Já há 5 extratos sendo lidos. Espere um deles terminar para enviar outro.")]
    public async Task TheLimitComesFromTheOption_AndTheMessageFollowsIt(int limit, string message)
    {
        var jobs = new FakeImportJobRepository();
        var svc = Build(jobs, new FakeStorageAdapter(), limit);
        for (var i = 0; i < limit; i++) await Upload(svc);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Upload(svc));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public async Task SimultaneousUploadsOfOneGroup_NeverPassTheLimit()
    {
        // One service per request, as in the API (the service is scoped); the repository answers the count late.
        var jobs = new FakeImportJobRepository();
        var storage = new FakeStorageAdapter();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            try { await Upload(Build(jobs, storage)); return true; }
            catch (ConflictException) { return false; }
        }));

        Assert.Equal(3, results.Count(ok => ok));
        Assert.Equal(3, jobs.Jobs.Count);
    }
}
