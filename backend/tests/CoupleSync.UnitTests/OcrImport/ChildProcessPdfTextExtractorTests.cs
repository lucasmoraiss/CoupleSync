using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker;
using Microsoft.Extensions.Logging;

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

    private readonly string _answersDirectory = Path.Combine(Path.GetTempPath(), "couplesync-pdfworker-answers-" + Guid.NewGuid().ToString("N"));

    public ChildProcessPdfTextExtractorTests() => Directory.CreateDirectory(_tempDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, recursive: true); } catch (IOException) { }
        try { if (Directory.Exists(_answersDirectory)) Directory.Delete(_answersDirectory, recursive: true); } catch (IOException) { }
    }

    // ── Same answers as the in-process reader ───────────────────────────────

    [Fact]
    public void AValidPdf_GivesTheSameTextAsTheInProcessReader()
    {
        var pdf = SyntheticPdf.Pages(3);
        var expected = new PdfPigTextExtractor().ExtractText(new MemoryStream(pdf));

        var logger = new ListLogger();

        var text = new ChildProcessPdfTextExtractor(Options(), logger).ExtractText(new MemoryStream(pdf));

        Assert.Equal(expected, text);
        Assert.Contains("Pagina 3 do extrato", text);
        // A clean read is one line of information: ending a child that has already ended is not a warning.
        Assert.Contains(logger.Lines, line => line.StartsWith("Information") && line.Contains("3 page(s)") && line.Contains("ok=True"));
        Assert.DoesNotContain(logger.Lines, line => line.StartsWith("Warning") || line.StartsWith("Error"));
    }

    [Theory]
    [InlineData("Hello World from CoupleSync bank statement extractor")]
    [InlineData("Hi")]
    public void TheCasesOfTheOldReaderTests_GiveTheSameTextThroughTheChild(string line)
    {
        // The same single-page documents PdfPigTextExtractorTests feeds the in-process reader (short content included).
        var expected = new PdfPigTextExtractor().ExtractText(PdfPigTextExtractorTests.BuildMinimalTextPdf(line));

        var text = new ChildProcessPdfTextExtractor(Options()).ExtractText(PdfPigTextExtractorTests.BuildMinimalTextPdf(line));

        Assert.Equal(expected, text);
        Assert.Contains(line, text);
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
    public async Task ARead_ThatNeverFinishes_FailsWithPdfTimeoutWithinTheLimit_AndTheChildIsGone()
    {
        // The idle child never reads its input, and 2 MB is far more than a pipe holds (a few KB on Windows, 64 KB on
        // Linux): writing the PDF blocks. That is the real stuck read, and it must not hold the caller either.
        var childId = 0;
        var extractor = new ChildProcessPdfTextExtractor(Idle(TimeSpan.FromSeconds(2), id => childId = id));
        var clock = Stopwatch.StartNew();

        var read = Task.Run(() => Assert.Throws<OcrException>(() => extractor.ExtractText(new MemoryStream(new byte[2 * 1024 * 1024]))));
        try
        {
            var first = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(first == read, "the read must end at the time limit even when the child takes none of the PDF");

            Assert.Equal("PDF_TIMEOUT", (await read).Code);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
            Assert.NotEqual(0, childId);
            Assert.True(IsGone(childId), "the child process must not exist after PDF_TIMEOUT");
        }
        finally
        {
            KillIfAlive(childId);
        }
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
        var heavy = SyntheticPdf.Pages(50, linesPerPage: 400);
        var extractor = new ChildProcessPdfTextExtractor(new PdfWorkerOptions { HeapHardLimitBytes = 6 * 1024 * 1024, TempDirectory = _tempDirectory });

        var ex = Assert.Throws<OcrException>(() => extractor.ExtractText(new MemoryStream(heavy)));

        Assert.Equal("PDF_WORKER_FAILED", ex.Code);

        // Control: the same document under the default limit is read normally, so the failure above is the limit.
        var text = new ChildProcessPdfTextExtractor(Options()).ExtractText(new MemoryStream(heavy));
        Assert.Contains("Pagina 50 do extrato", text);
    }

    /// <summary>
    /// The largest statements the reader in the API process could read on the small production instance, measured in
    /// the image (issue #14, round 1): about 10 MB in 50 pages when the size comes from pictures, and 50 pages of
    /// 700 lines each (35 thousand lines of pure text) when it comes from text. The real worker reads both
    /// under the default limits. Nothing here asks for speed: only that it ends well, with the default memory limit.
    /// </summary>
    [Theory]
    [InlineData(50, 45, 190_000, 9_000_000)]
    [InlineData(50, 700, 0, 1_500_000)]
    public void TheLargestStatementsReadBefore_AreStillReadUnderTheDefaultLimits(int pages, int linesPerPage, int imageBytesPerPage, int atLeastBytes)
    {
        var pdf = SyntheticPdf.Pages(pages, linesPerPage: linesPerPage, imageBytesPerPage: imageBytesPerPage);
        Assert.InRange(pdf.Length, atLeastBytes, 10 * 1024 * 1024);
        var defaults = new PdfWorkerOptions();
        var options = Options();
        var expected = new PdfPigTextExtractor(pages, Timeout.InfiniteTimeSpan).ExtractText(new MemoryStream(pdf));

        var text = new ChildProcessPdfTextExtractor(options).ExtractText(new MemoryStream(pdf));

        Assert.Equal(expected, text);
        Assert.Contains($"Pagina {pages} do extrato", text);
        Assert.Equal(defaults.HeapHardLimitBytes, options.HeapHardLimitBytes);
        Assert.Equal(defaults.Timeout, options.Timeout);
        Assert.Equal(defaults.MaxPages, options.MaxPages);
        Assert.Equal(192L * 1024 * 1024, PdfWorkerOptions.DefaultHeapHardLimitBytes);
    }

    [Fact]
    public void TheWorker_IsToldTheSizeOfThePdf_SoItHoldsItOnce()
    {
        var startInfo = new ChildProcessPdfTextExtractor(Options()).BuildStartInfo(new Hashtable(), inputBytes: 1234);

        var arguments = startInfo.ArgumentList.ToList();
        var index = arguments.IndexOf("--input-bytes");
        Assert.True(index >= 0);
        Assert.Equal("1234", arguments[index + 1]);

        // With the size, without it, or with a size that is wrong, the worker reads the same document.
        var pdf = SyntheticPdf.Pages(3);
        foreach (var args in new[] { new[] { "--pdf-worker" }, ["--pdf-worker", "--input-bytes", pdf.Length.ToString()], ["--pdf-worker", "--input-bytes", "7"], ["--pdf-worker", "--input-bytes", "999999999"] })
        {
            using var output = new MemoryStream();
            PdfWorkerHost.Run(args, new MemoryStream(pdf), output);
            using var answer = JsonDocument.Parse(output.ToArray());
            Assert.True(answer.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(3, answer.RootElement.GetProperty("pages").GetInt32());
        }
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

    [Fact]
    public void AChildThatDiesAlone_FailsWithoutWaitingForWhatItLeftBehind()
    {
        // Only the child is killed (not its tree): what it started keeps the pipes open. The read must fail at once
        // instead of waiting for those pipes, and on Windows (where the runtime can still find them) the leftovers go too.
        var started = DateTime.Now.AddSeconds(-1);
        var extractor = new ChildProcessPdfTextExtractor(IdleWithADescendant(TimeSpan.FromSeconds(30), id =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(700);
                Process.GetProcessById(id).Kill();
            });
        }));
        var clock = Stopwatch.StartNew();

        var ex = Assert.Throws<OcrException>(() => extractor.ExtractText(new MemoryStream(SyntheticPdf.Pages(2))));

        Assert.Equal("PDF_WORKER_FAILED", ex.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"a dead child must not be waited for; took {clock.Elapsed}");
        if (OperatingSystem.IsWindows())
            Assert.Empty(DescendantsStartedAfter(started));
    }

    [Fact]
    public void AKillThatFails_NeverReplacesPdfTimeout_AndIsLogged()
    {
        var logger = new ListLogger();
        var options = Idle(TimeSpan.FromSeconds(1), null) with
        {
            KillProcess = process =>
            {
                process.Kill(entireProcessTree: true);
                throw new AggregateException("Not all processes in process tree could be terminated.", new Win32Exception(5));
            },
        };

        var ex = Assert.Throws<OcrException>(() => new ChildProcessPdfTextExtractor(options, logger).ExtractText(new MemoryStream([1, 2, 3])));

        Assert.Equal("PDF_TIMEOUT", ex.Code);
        Assert.Contains(logger.Lines, line => line.StartsWith("Warning") && line.Contains("AggregateException"));
    }

    [Fact]
    public void AKillThatFails_DoesNotTurnAGoodReadIntoAnError()
    {
        var options = Options() with { KillProcess = _ => throw new Win32Exception(5) };

        var text = new ChildProcessPdfTextExtractor(options).ExtractText(new MemoryStream(SyntheticPdf.Pages(2)));

        Assert.Contains("Pagina 2 do extrato", text);
    }

    // ── The child is the one that goes when the memory of the container runs out ──

    [Fact]
    public void TheWorkerProcess_MakesItselfTheFirstToBeKilledForMemory_AndTheParentRecordsIt()
    {
        // The heap limit is not a limit of the whole process. When the container runs out of memory the kernel kills
        // the process with the highest score, and that must be the child, never the API (process 1: the container
        // would go down). The worker raises its own score at start (Linux only) and tells the value it read back.
        var logger = new ListLogger();

        new ChildProcessPdfTextExtractor(Options(), logger).ExtractText(new MemoryStream(SyntheticPdf.Pages(2)));

        var expected = OperatingSystem.IsLinux() ? "oom score adj=1000" : "oom score adj=-";
        Assert.Contains(logger.Lines, line => line.StartsWith("Information") && line.Contains("ok=True") && line.Contains(expected));
    }

    [Fact]
    public void TheWorkerProcess_LowersItsOwnPriority_SoTheApiGoesFirst()
    {
        // The child is watched from outside while it reads a document large enough to be seen running.
        var seen = new System.Collections.Concurrent.ConcurrentBag<ProcessPriorityClass>();
        var options = Options(id => _ = Task.Run(async () =>
        {
            try
            {
                using var child = Process.GetProcessById(id);
                while (!child.HasExited)
                {
                    child.Refresh();
                    seen.Add(child.PriorityClass);
                    await Task.Delay(10);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // The child ended between two looks.
            }
        }));

        new ChildProcessPdfTextExtractor(options).ExtractText(new MemoryStream(SyntheticPdf.Pages(50, linesPerPage: 200)));

        Assert.Contains(ProcessPriorityClass.BelowNormal, seen);
    }

    [Fact]
    public void ReadingInsideAnotherProcess_DoesNotTouchTheScoreOfThatProcess()
    {
        // PdfWorkerHost.Run is also called by tests, inside the test process: only the entry of the real worker
        // process (RunProcess) changes the score.
        var before = OperatingSystem.IsLinux() ? File.ReadAllText("/proc/self/oom_score_adj") : null;
        using var output = new MemoryStream();

        PdfWorkerHost.Run([PdfWorkerHost.Command], new MemoryStream(SyntheticPdf.Pages(1)), output);

        using var answer = JsonDocument.Parse(output.ToArray());
        Assert.False(answer.RootElement.TryGetProperty("oomScoreAdj", out _));
        if (OperatingSystem.IsLinux())
            Assert.Equal(before, File.ReadAllText("/proc/self/oom_score_adj"));
    }

    // ── What comes back from the child ──────────────────────────────────────

    [Fact]
    public void TheWorker_AnswersAnUnreadableFileWithTheExceptionTypeName_NeverItsMessage()
    {
        var garbage = "conteudo-secreto-do-arquivo"u8.ToArray();
        var thrown = Record.Exception(() => new PdfPigTextExtractor().ExtractText(new MemoryStream(garbage)));
        Assert.NotNull(thrown);
        Assert.False(string.IsNullOrWhiteSpace(thrown.Message));
        using var output = new MemoryStream();

        PdfWorkerHost.Run([PdfWorkerHost.Command], new MemoryStream(garbage), output);

        var json = Encoding.UTF8.GetString(output.ToArray());
        using var answer = JsonDocument.Parse(json);
        Assert.Equal("PDF_UNREADABLE", answer.RootElement.GetProperty("code").GetString());
        Assert.Equal(thrown.GetType().Name, answer.RootElement.GetProperty("errorType").GetString());
        Assert.DoesNotContain(thrown.Message, json);
        Assert.DoesNotContain(thrown.Message, answer.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("secreto", json);
    }

    [Fact]
    public void AnUnreadableFile_IsLoggedWithTheCodeAndTheExceptionType_NeverTheMessageOrTheContent()
    {
        var garbage = "conteudo-secreto-do-arquivo"u8.ToArray();
        var thrown = Record.Exception(() => new PdfPigTextExtractor().ExtractText(new MemoryStream(garbage)));
        Assert.NotNull(thrown);
        var logger = new ListLogger();

        var ex = Assert.Throws<OcrException>(() => new ChildProcessPdfTextExtractor(Options(), logger).ExtractText(new MemoryStream(garbage)));

        Assert.Equal("PDF_UNREADABLE", ex.Code);
        Assert.Contains(logger.Lines, line => line.Contains("PDF_UNREADABLE") && line.Contains(thrown.GetType().Name));
        Assert.DoesNotContain(logger.Lines, line => line.Contains(thrown.Message) || line.Contains("secreto"));
        Assert.DoesNotContain(thrown.Message, ex.Message);
    }

    [Fact]
    public void ACodeTheParentDoesNotKnow_BecomesAFailedWorkerWithTheFixedMessage()
    {
        var extractor = new ChildProcessPdfTextExtractor(Answering("""{"ok":false,"code":"CODIGO_INVENTADO","message":"mensagem escrita pelo filho"}"""));

        var ex = Assert.Throws<OcrException>(() => extractor.ExtractText(new MemoryStream([1, 2, 3])));

        Assert.Equal("PDF_WORKER_FAILED", ex.Code);
        Assert.Equal("A leitura do PDF falhou de forma inesperada. Tente enviar o arquivo novamente.", ex.Message);
    }

    [Fact]
    public void ACodeTheParentKnows_PassesWithItsMessage()
    {
        var extractor = new ChildProcessPdfTextExtractor(Answering("""{"ok":false,"code":"PDF_TOO_MANY_PAGES","message":"mensagem do limite"}"""));

        var ex = Assert.Throws<OcrException>(() => extractor.ExtractText(new MemoryStream([1, 2, 3])));

        Assert.Equal("PDF_TOO_MANY_PAGES", ex.Code);
        Assert.Equal("mensagem do limite", ex.Message);
    }

    [Fact]
    public void AnExceptionTypeThatIsNotAPlainName_IsNotLogged()
    {
        var logger = new ListLogger();
        var extractor = new ChildProcessPdfTextExtractor(
            Answering("""{"ok":false,"code":"PDF_UNREADABLE","message":"m","errorType":"Tipo com texto do documento: saldo 123"}"""), logger);

        var ex = Assert.Throws<OcrException>(() => extractor.ExtractText(new MemoryStream([1, 2, 3])));

        Assert.Equal("PDF_UNREADABLE", ex.Code);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("saldo") || line.Contains("texto do documento"));
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
    public void TheEnvironmentOfTheChild_IsAnAllowList_SoSecretsAddedLaterStayOut()
    {
        // Secrets that exist today and names nobody has invented yet: none of them may reach the child, because the
        // child gets only what is on the list (runtime and globalization), never "everything except what is known".
        var parent = new Hashtable
        {
            ["PATH"] = "/usr/bin",
            ["OPENFINANCE_ENCRYPTION_KEY"] = "chave-de-cifragem",
            ["OpenFinance__EncryptionKey"] = "chave-de-cifragem-2",
            ["PLUGGY_CLIENT_ID"] = "id-pluggy",
            ["PLUGGY_CLIENT_SECRET"] = "segredo-pluggy",
            ["OpenFinance__PluggyClientSecret"] = "segredo-pluggy-2",
            ["INTERNAL_JOBS_SECRET"] = "segredo-de-jobs",
            ["AI__ApiKey"] = "chave-de-ia-2",
            ["ANTHROPIC_API_KEY"] = "chave-de-ia-3",
            // The AI gateway (issue #37): the key of each provider and the settings that name it.
            ["GROQ_API_KEY"] = "chave-de-ia-4",
            ["Ai__OpenAiCompatible__0__ApiKeyVariable"] = "GROQ_API_KEY",
            ["Ai__OpenAiCompatible__0__BaseUrl"] = "https://provedor.invalid/v1",
            ["Ai__Chains__Chat__0"] = "groq",
            ["SEGREDO_QUE_AINDA_NAO_EXISTE"] = "valor-novo",
        };
        var extractor = new ChildProcessPdfTextExtractor(Options());

        var startInfo = extractor.BuildStartInfo(parent);

        var names = startInfo.Environment.Keys.ToList();
        foreach (var forbidden in new[]
        {
            "OPENFINANCE_ENCRYPTION_KEY", "OpenFinance__EncryptionKey", "PLUGGY_CLIENT_ID", "PLUGGY_CLIENT_SECRET",
            "OpenFinance__PluggyClientSecret", "INTERNAL_JOBS_SECRET", "AI__ApiKey", "ANTHROPIC_API_KEY", "SEGREDO_QUE_AINDA_NAO_EXISTE",
            "GROQ_API_KEY", "Ai__OpenAiCompatible__0__ApiKeyVariable", "Ai__OpenAiCompatible__0__BaseUrl", "Ai__Chains__Chat__0",
        })
            Assert.DoesNotContain(forbidden, names);
        Assert.DoesNotContain(startInfo.Environment.Values, v => v is not null && (v.StartsWith("chave-") || v.StartsWith("segredo") || v.StartsWith("id-") || v == "valor-novo" || v == "GROQ_API_KEY" || v == "groq" || v.Contains("provedor.invalid")));
        Assert.Equal("/usr/bin", startInfo.Environment["PATH"]);
    }

    [Fact]
    public void TheEnvironmentOfTheChild_LimitsTheHeap()
    {
        var child = ChildProcessPdfTextExtractor.BuildChildEnvironment(new Hashtable(), new PdfWorkerOptions());

        Assert.Equal("c000000", child["DOTNET_GCHeapHardLimit"]);
    }

    [Fact]
    public void TheEnvironmentOfTheChild_FixesTheGarbageCollector_WhateverTheParentHasAndHoweverManyCoresThereAre()
    {
        // The API is a web project (server GC by default, one heap per core). The child was measured with the
        // workstation, non-concurrent collector, and must run with it on any machine.
        var parent = new Hashtable { ["DOTNET_gcServer"] = "1", ["COMPlus_gcServer"] = "1", ["DOTNET_GCHeapCount"] = "c", ["DOTNET_gcConcurrent"] = "1" };

        var child = new ChildProcessPdfTextExtractor(Options()).BuildStartInfo(parent).Environment;

        Assert.Equal("0", child["DOTNET_gcServer"]);
        Assert.Equal("0", child["DOTNET_gcConcurrent"]);
        Assert.DoesNotContain("COMPlus_gcServer", child.Keys);
        Assert.DoesNotContain("DOTNET_GCHeapCount", child.Keys);
    }

    [Fact]
    public void ThePageLimitIsThatOfTheOldReader_AndTheTimeLimitCoversTheStartOfTheProcess()
    {
        var options = new PdfWorkerOptions();

        Assert.Equal(50, options.MaxPages);
        // 30 s was the limit of a read inside the API, already started and compiled. The child starts and compiles
        // for every file, and on the 0.1 CPU of the production instance that alone takes about 10 s: with 30 s it
        // refused statements the old reader read. 120 s is what the measurement in the image supports (rounds 1 and 2
        // of the review of issue #14): the largest statement the old reader read there took 49 s through the child
        // alone, and up to 73 s inside the API while it answers one request per second.
        Assert.Equal(TimeSpan.FromSeconds(120), options.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(120), PdfWorkerOptions.DefaultTimeout);
        // Still far below the 10 minutes after which a job stuck in "processing" is given up on.
        Assert.True(options.Timeout < CoupleSync.Application.OcrImport.ImportJobRecovery.ProcessingTimeout / 4);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private PdfWorkerOptions Options(Action<int>? onStarted = null) => new() { TempDirectory = _tempDirectory, OnProcessStarted = onStarted };

    /// <summary>A "worker" that reads nothing and never answers: an idle shell command (the same on Windows and Linux).</summary>
    private PdfWorkerOptions Idle(TimeSpan timeout, Action<int>? onStarted) =>
        IdleWorkerOptions() with { Timeout = timeout, TempDirectory = _tempDirectory, OnProcessStarted = onStarted };

    /// <summary>The program of an idle "worker", shared by the tests that need a read that never finishes.</summary>
    internal static PdfWorkerOptions IdleWorkerOptions() => OperatingSystem.IsWindows()
        ? new PdfWorkerOptions { FileName = "cmd.exe", Arguments = ["/c", "ping -n 60 127.0.0.1 > nul"] }
        : new PdfWorkerOptions { FileName = "sh", Arguments = ["-c", "sleep 60"] };

    /// <summary>An idle child whose own child has a name nothing else on the machine uses (Windows), so the test can look for it.</summary>
    private PdfWorkerOptions IdleWithADescendant(TimeSpan timeout, Action<int>? onStarted) => OperatingSystem.IsWindows()
        ? new PdfWorkerOptions { FileName = "cmd.exe", Arguments = ["/c", "waitfor /t 60 CoupleSyncPdfWorkerTestNeverSent"], Timeout = timeout, TempDirectory = _tempDirectory, OnProcessStarted = onStarted }
        : new PdfWorkerOptions { FileName = "sh", Arguments = ["-c", "sleep 60; true"], Timeout = timeout, TempDirectory = _tempDirectory, OnProcessStarted = onStarted };

    private static List<int> DescendantsStartedAfter(DateTime moment)
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName("waitfor"))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.StartTime >= moment)
                        found.Add(process.Id);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Gone between the listing and the look.
                }
            }
        }

        return found;
    }

    /// <summary>A "worker" that reads nothing and prints a prepared answer (kept outside the temp directory of the child).</summary>
    private PdfWorkerOptions Answering(string json)
    {
        Directory.CreateDirectory(_answersDirectory);
        var path = Path.Combine(_answersDirectory, "answer.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return OperatingSystem.IsWindows()
            ? new PdfWorkerOptions { FileName = "cmd.exe", Arguments = ["/c", $"type {path}"], TempDirectory = _tempDirectory }
            : new PdfWorkerOptions { FileName = "sh", Arguments = ["-c", $"cat '{path}'"], TempDirectory = _tempDirectory };
    }

    private static void KillIfAlive(int processId)
    {
        if (processId == 0)
            return;
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or AggregateException)
        {
            // Already gone.
        }
    }

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

    private sealed class ListLogger : ILogger<ChildProcessPdfTextExtractor>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // The exception, when one is attached, is part of what a real logger would print.
            lock (_lines) _lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
        }
    }
}
