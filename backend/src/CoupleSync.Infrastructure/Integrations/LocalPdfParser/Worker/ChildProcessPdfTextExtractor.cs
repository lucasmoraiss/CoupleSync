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
        // A one-shot worker needs neither the diagnostics socket nor the background GC thread.
        child["DOTNET_EnableDiagnostics"] = "0";
        child["DOTNET_gcConcurrent"] = "0";
        child["DOTNET_TieredPGO"] = "0";
        child["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        child["DOTNET_NOLOGO"] = "1";
        return child;
    }

    public string ExtractText(Stream pdfStream)
    {
        byte[] input;
        using (var buffer = new MemoryStream())
        {
            pdfStream.CopyTo(buffer);
            input = buffer.ToArray();
        }

        var clock = Stopwatch.StartNew();
        var startInfo = BuildStartInfo();

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
            try
            {
                _options.OnProcessStarted?.Invoke(process.Id);

                var writing = Task.Run(() => WriteInput(process, input));
                var reading = process.StandardOutput.ReadToEndAsync();
                var drainingErrors = process.StandardError.ReadToEndAsync(); // never looked at: it could quote the PDF

                var remaining = _options.Timeout - clock.Elapsed;
                if (remaining < TimeSpan.Zero || !process.WaitForExit(remaining))
                {
                    Kill(process);
                    _logger.LogWarning("PDF worker killed after the {TimeoutMs}ms limit.", (int)_options.Timeout.TotalMilliseconds);
                    throw new OcrException(
                        "PDF_TIMEOUT",
                        "A leitura do PDF demorou demais e foi interrompida. Envie um extrato menor ou exporte o PDF novamente.");
                }

                Task.WaitAll([writing, reading, drainingErrors], PipeDrainWait);
                var exitCode = process.ExitCode;
                var answer = exitCode == 0 && reading.IsCompletedSuccessfully ? TryParse(reading.Result) : null;

                if (answer is null)
                {
                    _logger.LogError("PDF worker ended without a usable answer (exit code {ExitCode}, {ElapsedMs}ms).", exitCode, clock.ElapsedMilliseconds);
                    throw WorkerFailed();
                }

                _logger.LogInformation(
                    "PDF worker finished (exit code {ExitCode}, {ElapsedMs}ms, {Pages} page(s), ok={Ok}).",
                    exitCode, clock.ElapsedMilliseconds, answer.Pages, answer.Ok);

                if (!answer.Ok)
                    throw new OcrException(answer.Code ?? PdfWorkerHost.WorkerFailedCode, answer.Message ?? WorkerFailed().Message);

                return answer.Text ?? string.Empty;
            }
            finally
            {
                // Every way out (answer, error, timeout, exception) leaves nothing running.
                Kill(process);
            }
        }
    }

    /// <summary>What is started for one file, with the environment already reduced to the allow-list.</summary>
    public ProcessStartInfo BuildStartInfo(IDictionary? parentEnvironment = null)
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

    private static void WriteInput(Process process, byte[] input)
    {
        try
        {
            using var stdin = process.StandardInput.BaseStream;
            stdin.Write(input, 0, input.Length);
        }
        catch (IOException)
        {
            // The child ended (or was killed) before taking everything: the exit code tells the story.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

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
