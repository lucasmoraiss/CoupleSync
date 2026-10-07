namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker;

/// <summary>How the child process that reads one PDF is started and limited.</summary>
public sealed record PdfWorkerOptions
{
    /// <summary>Limit of the managed heap of the child (DOTNET_GCHeapHardLimit): past it the child dies, not the API.</summary>
    public const long DefaultHeapHardLimitBytes = 128L * 1024 * 1024;

    /// <summary>Executable to start; null means the API executable itself, run as the PDF worker.</summary>
    public string? FileName { get; init; }

    /// <summary>Arguments for <see cref="FileName"/>; ignored when it is null.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public int MaxPages { get; init; } = PdfPigTextExtractor.DefaultMaxPages;

    /// <summary>Counts from just before the process is started, so the start-up time is inside it.</summary>
    public TimeSpan Timeout { get; init; } = PdfPigTextExtractor.DefaultTimeout;

    public long HeapHardLimitBytes { get; init; } = DefaultHeapHardLimitBytes;

    /// <summary>When set, the child gets this directory as its temp directory instead of the parent one.</summary>
    public string? TempDirectory { get; init; }

    /// <summary>Called with the id of the child right after it started (used by the tests to check it is gone).</summary>
    public Action<int>? OnProcessStarted { get; init; }
}
