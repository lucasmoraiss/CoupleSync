using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker;

/// <summary>
/// Reads a PDF in a child process created for that one file and ended right after. PdfPig is synchronous and cannot
/// be cancelled, so the only way to honour the time limit is to kill the process that runs it; the same goes for a
/// document that eats memory (the child has a heap limit) or crashes the runtime: the API keeps running.
/// The PDF goes in through the standard input and the answer comes out of the standard output; no file is written.
/// </summary>
public sealed class ChildProcessPdfTextExtractor : IPdfTextExtractor
{
    private static readonly TimeSpan PipeDrainWait = TimeSpan.FromSeconds(5);

    /// <summary>Variables the child may inherit; everything else (connection string, secrets, API keys) stays out.</summary>
    private static readonly string[] InheritedNames =
    [
        "PATH", "HOME", "LANG", "LC_ALL", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64",
        // Windows needs these to start a process at all.
        "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
    ];

    private static readonly string[] TempNames = ["TMPDIR", "TEMP", "TMP"];

    /// <summary>The only error codes the worker sends; anything else in an answer is treated as a failed worker.</summary>
    private static readonly string[] KnownAnswerCodes = ["PDF_ENCRYPTED", "PDF_TOO_MANY_PAGES", PdfWorkerHost.UnreadableCode];

    private readonly PdfWorkerOptions _options;
    private readonly ILogger<ChildProcessPdfTextExtractor> _logger;

    public ChildProcessPdfTextExtractor(ILogger<ChildProcessPdfTextExtractor> logger)
        : this(new PdfWorkerOptions(), logger)
    {
    }

    public ChildProcessPdfTextExtractor(PdfWorkerOptions options, ILogger<ChildProcessPdfTextExtractor>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<ChildProcessPdfTextExtractor>.Instance;
    }

    /// <summary>The environment the child gets, built from the parent one: an allow-list, never a copy.</summary>
    public static IReadOnlyDictionary<string, string> BuildChildEnvironment(IDictionary parentEnvironment, PdfWorkerOptions options)
    {
        var child = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (DictionaryEntry entry in parentEnvironment)
        {
            if (entry.Key is not string name || entry.Value is not string value)
                continue;

            var inherited = Array.Exists(InheritedNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                // Globalization settings must match the API ones, or the child would read text in another mode.
                || name.StartsWith("DOTNET_SYSTEM_GLOBALIZATION", StringComparison.OrdinalIgnoreCase)
                || (options.TempDirectory is null && Array.Exists(TempNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
            if (inherited)
                child[name] = value;
        }

        if (options.TempDirectory is not null)
            foreach (var name in TempNames)
                child[name] = options.TempDirectory;

        child["DOTNET_GCHeapHardLimit"] = options.HeapHardLimitBytes.ToString("x", CultureInfo.InvariantCulture);
        // A one-shot worker needs neither the diagnostics socket nor the background GC thread. The API is a web project
        // (server GC by default: one heap per core the machine shows); the child always gets the workstation collector,
        // so that the memory it takes under the limit above is the same on one core or on many.
        child["DOTNET_EnableDiagnostics"] = "0";
        child["DOTNET_gcServer"] = "0";
        child["DOTNET_gcConcurrent"] = "0";
        child["DOTNET_TieredPGO"] = "0";
        child["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        child["DOTNET_NOLOGO"] = "1";
        return child;
    }

    public string ExtractText(Stream pdfStream)
    {
        // One copy of the PDF in this process: the buffer the stream is read into is the one written to the child.
        using var buffer = pdfStream.CanSeek
            ? new MemoryStream((int)Math.Clamp(pdfStream.Length - pdfStream.Position, 0, Array.MaxLength))
            : new MemoryStream();
        pdfStream.CopyTo(buffer);
        var input = new ReadOnlyMemory<byte>(buffer.GetBuffer(), 0, (int)buffer.Length);

        var clock = Stopwatch.StartNew();
        var startInfo = BuildStartInfo(inputBytes: input.Length);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("no process");
        }
        catch (Exception ex)
        {
            _logger.LogError("PDF worker could not be started ({ExceptionType}).", ex.GetType().Name);
            throw WorkerFailed();
        }

        using (process)
        {
            var killed = false;
            try
            {
                _options.OnProcessStarted?.Invoke(process.Id);

                var writing = Task.Run(() => WriteInput(process, input));
                var reading = process.StandardOutput.ReadToEndAsync();
                var drainingErrors = process.StandardError.ReadToEndAsync(); // never looked at: it could quote the PDF

                var remaining = _options.Timeout - clock.Elapsed;
                if (remaining < TimeSpan.Zero || !process.WaitForExit(remaining))
                {
                    killed = true;
                    Kill(process);
                    _logger.LogWarning("PDF worker killed after the {TimeoutMs}ms limit.", (int)_options.Timeout.TotalMilliseconds);
                    throw new OcrException(
                        "PDF_TIMEOUT",
                        "A leitura do PDF demorou demais e foi interrompida. Envie um extrato menor ou exporte o PDF novamente.");
                }

                // The child has ended. Whatever it may have started goes now, before the pipes are waited for: a
                // leftover process would keep them open. A child that ended badly has no answer worth waiting for.
                killed = true;
                Kill(process);
                var exitCode = process.ExitCode;
                var answer = exitCode == 0 && Drained(writing, reading, drainingErrors) ? TryParse(reading.Result) : null;

                if (answer is null)
                {
                    _logger.LogError("PDF worker ended without a usable answer (exit code {ExitCode}, {ElapsedMs}ms).", exitCode, clock.ElapsedMilliseconds);
                    throw WorkerFailed();
                }

                var known = answer.Ok || (Array.IndexOf(KnownAnswerCodes, answer.Code) >= 0 && !string.IsNullOrWhiteSpace(answer.Message));
                if (!known)
                {
                    // The code is not repeated here nor to the user: it did not come from the list, so it is not trusted.
                    _logger.LogError("PDF worker answered with a code that is not known (exit code {ExitCode}, {ElapsedMs}ms).", exitCode, clock.ElapsedMilliseconds);
                    throw WorkerFailed();
                }

                _logger.LogInformation(
                    "PDF worker finished (exit code {ExitCode}, {ElapsedMs}ms, {Pages} page(s), ok={Ok}, code={Code}, error type={ErrorType}, oom score adj={OomScoreAdj}).",
                    exitCode, clock.ElapsedMilliseconds, answer.Pages, answer.Ok, answer.Code ?? "-", PlainTypeName(answer.ErrorType) ?? "-",
                    answer.OomScoreAdj?.ToString(CultureInfo.InvariantCulture) ?? "-");

                if (!answer.Ok)
                    throw new OcrException(answer.Code!, answer.Message!);

                return answer.Text ?? string.Empty;
            }
            finally
            {
                // Every way out (answer, error, timeout, exception) leaves nothing running.
                if (!killed)
                    Kill(process);
            }
        }
    }

    /// <summary>What is started for one file, with the environment already reduced to the allow-list.</summary>
    public ProcessStartInfo BuildStartInfo(IDictionary? parentEnvironment = null, int inputBytes = 0)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (_options.FileName is null)
        {
            startInfo.FileName = HostExecutable();
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CoupleSync.Api.dll"));
            startInfo.ArgumentList.Add(PdfWorkerHost.Command);
            startInfo.ArgumentList.Add(PdfWorkerHost.MaxPagesArgument);
            startInfo.ArgumentList.Add(_options.MaxPages.ToString(CultureInfo.InvariantCulture));
            if (inputBytes > 0)
            {
                // Lets the worker take the PDF into a buffer of the right size instead of one that grows by doubling.
                startInfo.ArgumentList.Add(PdfWorkerHost.InputBytesArgument);
                startInfo.ArgumentList.Add(inputBytes.ToString(CultureInfo.InvariantCulture));
            }
        }
        else
        {
            startInfo.FileName = _options.FileName;
            foreach (var argument in _options.Arguments)
                startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var (name, value) in BuildChildEnvironment(parentEnvironment ?? Environment.GetEnvironmentVariables(), _options))
            startInfo.Environment[name] = value;
        return startInfo;
    }

    /// <summary>The `dotnet` that runs the API, or the one on the PATH when the host is something else (a test runner).</summary>
    private static string HostExecutable()
    {
        var current = Environment.ProcessPath;
        return current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? current
            : "dotnet";
    }

    private static void WriteInput(Process process, ReadOnlyMemory<byte> input)
    {
        try
        {
            using var stdin = process.StandardInput.BaseStream;
            stdin.Write(input.Span);
        }
        catch (IOException)
        {
            // The child ended (or was killed) before taking everything: the exit code tells the story.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>True when the three pipes were read or written to the end within the wait.</summary>
    private static bool Drained(Task writing, Task<string> reading, Task<string> drainingErrors)
    {
        try
        {
            return Task.WaitAll([writing, reading, drainingErrors], PipeDrainWait) && reading.IsCompletedSuccessfully;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ends the child and everything it started. It is called on every way out and never throws: a kill that fails
    /// (access denied, part of the tree that could not be ended) must not replace the answer or the error already
    /// decided, so it is only recorded. It is also called when the child itself has already ended, for what it may
    /// have left behind: Windows still finds those processes; on Linux a process that ended leaves no trail to its
    /// children, and there the runtime does nothing (the real worker starts no process).
    /// </summary>
    private void Kill(Process process)
    {
        try
        {
            if (_options.KillProcess is { } kill)
                kill(process);
            else
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("PDF worker could not be killed cleanly ({ExceptionType}).", ex.GetType().Name);
        }
    }

    /// <summary>The exception type sent by the worker, only when it looks like a type name (it goes to the log).</summary>
    private static string? PlainTypeName(string? value) =>
        value is { Length: > 0 and <= 120 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '`' or '+')
            ? value
            : null;

    private static WorkerAnswer? TryParse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkerAnswer>(text, PdfWorkerHost.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OcrException WorkerFailed() => new(
        PdfWorkerHost.WorkerFailedCode,
        "A leitura do PDF falhou de forma inesperada. Tente enviar o arquivo novamente.");
}
