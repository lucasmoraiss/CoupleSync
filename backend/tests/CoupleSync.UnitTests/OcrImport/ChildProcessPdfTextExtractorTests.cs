using System.Collections;
using System.Diagnostics;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>
/// Issue #14: the PDF is read by a child process that is created for the file and killed when the time is over,
/// so a stuck or hostile PDF cannot hold the API. These tests start real processes (the API executable as the worker
/// for real PDFs, an idle shell command for the "never finishes" cases).
/// </summary>
[Trait("Category", "PdfExtraction")]
public sealed class ChildProcessPdfTextExtractorTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "couplesync-pdfworker-test-" + Guid.NewGuid().ToString("N"));

    public ChildProcessPdfTextExtractorTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch (IOException) { }
    }

    // ── Same answers as the in-process reader ───────────────────────────────

    [Fact]
    public void AValidPdf_GivesTheSameTextAsTheInProcessReader()
    {
        var pdf = SyntheticPdf.Pages(3);
        var expected = new PdfPigTextExtractor().ExtractText(new MemoryStream(pdf));

        var text = new ChildProcessPdfTextExtractor(Options()).ExtractText(new MemoryStream(pdf));

        Assert.Equal(expected, text);
        Assert.Contains("Pagina 3 do extrato", text);
    }

    [Fact]
    public void APdfWithAPassword_FailsWithPdfEncrypted()
    {
        var ex = Assert.Throws<OcrException>(() =>
            new ChildProcessPdfTextExtractor(Options()).ExtractText(new MemoryStream(SyntheticPdf.Pages(1, encrypted: true))));

        Assert.Equal("PDF_ENCRYPTED", ex.Code);
        Assert.Equal("O PDF está protegido por senha. Exporte sem senha e tente novamente.", ex.Message);
    }

    [Fact]
    public void MoreThan50Pages_FailWithPdfTooManyPages_AndTheSameMessageAsBefore()
    {
        var pdf = SyntheticPdf.Pages(51);
        var inProcess = Assert.Throws<OcrException>(() => new PdfPigTextExtractor().ExtractText(new MemoryStream(pdf)));

        var ex = Assert.Throws<OcrException>(() => new ChildProcessPdfTextExtractor(Options()).ExtractText(new MemoryStream(pdf)));

        Assert.Equal("PDF_TOO_MANY_PAGES", ex.Code);
        Assert.Equal(inProcess.Message, ex.Message);
        Assert.Contains("51 páginas; o limite é 50", ex.Message);
    }

    [Fact]
    public void AFileThatIsNotAPdf_FailsWithAFixedMessage_NeverQuotingTheFile()
    {
        var ex = Assert.Throws<OcrException>(() =>
            new ChildProcessPdfTextExtractor(Options()).ExtractText(new MemoryStream("conteudo-secreto-do-arquivo"u8.ToArray())));

        Assert.Equal("PDF_UNREADABLE", ex.Code);
        Assert.DoesNotContain("secreto", ex.Message);
    }

    // ── The child is killed, and nothing is left ────────────────────────────

    [Fact]
    public void ARead_ThatNeverFinishes_FailsWithPdfTimeoutWithinTheLimit_AndTheChildIsGone()
    {
        var childId = 0;
        var extractor = new ChildProcessPdfTextExtractor(Idle(TimeSpan.FromSeconds(2), id => childId = id));
        var clock = Stopwatch.StartNew();

        var ex = Assert.Throws<OcrException>(() => extractor.ExtractText(new MemoryStream([1, 2, 3])));

        Assert.Equal("PDF_TIMEOUT", ex.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.NotEqual(0, childId);
        Assert.True(IsGone(childId), "the child process must not exist after PDF_TIMEOUT");
    }

    [Fact]
    public void AChildKilledInTheMiddleOfTheRead_FailsWithItsOwnCode_Quickly_AndLeavesNothing()
    {
        var childId = 0;
        var extractor = new ChildProcessPdfTextExtractor(Idle(TimeSpan.FromSeconds(30), id =>
        {
            childId = id;
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                Process.GetProcessById(id).Kill(entireProcessTree: true);
            });
        }));
        var clock = Stopwatch.StartNew();

        var ex = Assert.Throws<OcrException>(() => extractor.ExtractText(new MemoryStream([1, 2, 3])));

        Assert.Equal("PDF_WORKER_FAILED", ex.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "a dead child must not wait for the time limit");
        Assert.True(IsGone(childId));
        Assert.Empty(Directory.GetFileSystemEntries(_tempDirectory));
    }

    [Fact]
    public void AReadThatNeedsMoreMemoryThanTheLimit_KillsOnlyTheChild_AndFailsWithTheWorkerCode()
    {
        // 6 MB of heap is far below what reading 50 text pages takes: the runtime of the child dies, the test process lives.
        var extractor = new ChildProcessPdfTextExtractor(new PdfWorkerOptions { HeapHardLimitBytes = 6 * 1024 * 1024, TempDirectory = _tempDirectory });

        var ex = Assert.Throws<OcrException>(() => extractor.ExtractText(new MemoryStream(SyntheticPdf.Pages(50, linesPerPage: 400))));

        Assert.Equal("PDF_WORKER_FAILED", ex.Code);
    }

    [Fact]
    public void AProgramThatCannotBeStarted_FailsWithTheWorkerCode()
    {
        var extractor = new ChildProcessPdfTextExtractor(new PdfWorkerOptions { FileName = "couplesync-no-such-program", TempDirectory = _tempDirectory });

        var ex = Assert.Throws<OcrException>(() => extractor.ExtractText(new MemoryStream([1])));

        Assert.Equal("PDF_WORKER_FAILED", ex.Code);
    }

    [Fact]
    public async Task OneStuckRead_DoesNotStopAValidReadHappeningAtTheSameTime()
    {
        var stuck = new ChildProcessPdfTextExtractor(Idle(TimeSpan.FromSeconds(4), null));
        var valid = new ChildProcessPdfTextExtractor(Options());

        var stuckRead = Task.Run(() => Assert.Throws<OcrException>(() => stuck.ExtractText(new MemoryStream([1]))));
        var text = await Task.Run(() => valid.ExtractText(new MemoryStream(SyntheticPdf.Pages(2))));

        Assert.Contains("Pagina 2 do extrato", text);
        Assert.Equal("PDF_TIMEOUT", (await stuckRead).Code);
    }

    [Fact]
    public void NoTemporaryFileIsLeft_OnSuccess_OnError_OnTimeout_OrWhenTheChildDies()
    {
        var pdf = SyntheticPdf.Pages(2);

        new ChildProcessPdfTextExtractor(Options()).ExtractText(new MemoryStream(pdf));
        Assert.Throws<OcrException>(() => new ChildProcessPdfTextExtractor(Options()).ExtractText(new MemoryStream(SyntheticPdf.Pages(1, encrypted: true))));
        Assert.Throws<OcrException>(() => new ChildProcessPdfTextExtractor(Idle(TimeSpan.FromSeconds(1), null)).ExtractText(new MemoryStream(pdf)));
        Assert.Throws<OcrException>(() => new ChildProcessPdfTextExtractor(Idle(TimeSpan.FromSeconds(30), id => Process.GetProcessById(id).Kill())).ExtractText(new MemoryStream(pdf)));

        Assert.Empty(Directory.GetFileSystemEntries(_tempDirectory));
    }

    // ── What the child gets ─────────────────────────────────────────────────

    [Fact]
    public void TheEnvironmentOfTheChild_HasNoSecretAndNoConnectionString()
    {
        var parent = new Hashtable
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/appuser",
            ["DATABASE_URL"] = "Host=x;Password=y",
            ["ConnectionStrings__Default"] = "Host=x",
            ["JWT__SECRET"] = "segredo",
            ["Email__ApiKey"] = "chave-de-email",
            ["GEMINI_API_KEY"] = "chave-de-ia",
            ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "false",
            ["ASPNETCORE_URLS"] = "http://+:8080",
        };

        var child = ChildProcessPdfTextExtractor.BuildChildEnvironment(parent, new PdfWorkerOptions());

        foreach (var forbidden in new[] { "DATABASE_URL", "ConnectionStrings__Default", "JWT__SECRET", "Email__ApiKey", "GEMINI_API_KEY", "ASPNETCORE_URLS" })
            Assert.DoesNotContain(forbidden, child.Keys);
        Assert.DoesNotContain(child.Values, v => v is "segredo" or "chave-de-email" or "chave-de-ia" || v.Contains("Password"));
        Assert.Equal("/usr/bin", child["PATH"]);
        Assert.Equal("false", child["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"]);
    }

    [Fact]
    public void TheEnvironmentOfTheChild_LimitsTheHeap()
    {
        var child = ChildProcessPdfTextExtractor.BuildChildEnvironment(new Hashtable(), new PdfWorkerOptions());

        Assert.Equal("8000000", child["DOTNET_GCHeapHardLimit"]);
    }

    [Fact]
    public void TheLimitsAreStillThoseOfTheOldReader()
    {
        var options = new PdfWorkerOptions();

        Assert.Equal(50, options.MaxPages);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private PdfWorkerOptions Options(Action<int>? onStarted = null) => new() { TempDirectory = _tempDirectory, OnProcessStarted = onStarted };

    /// <summary>A "worker" that reads nothing and never answers: an idle shell command (the same on Windows and Linux).</summary>
    private PdfWorkerOptions Idle(TimeSpan timeout, Action<int>? onStarted) => OperatingSystem.IsWindows()
        ? new PdfWorkerOptions { FileName = "cmd.exe", Arguments = ["/c", "ping -n 60 127.0.0.1 > nul"], Timeout = timeout, TempDirectory = _tempDirectory, OnProcessStarted = onStarted }
        : new PdfWorkerOptions { FileName = "sh", Arguments = ["-c", "sleep 60"], Timeout = timeout, TempDirectory = _tempDirectory, OnProcessStarted = onStarted };

    private static bool IsGone(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
