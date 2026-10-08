namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker;

/// <summary>How the child process that reads one PDF is started and limited.</summary>
public sealed record PdfWorkerOptions
{
    /// <summary>
    /// Limit of the managed heap of the child (DOTNET_GCHeapHardLimit): past it the child dies, not the API.
    /// Measured in the image (issue #14, review round 1): the largest statement the reader inside the API could read
    /// on the production instance (50 pages, 35 thousand lines of text) needs 96 MB of heap; statements whose size
    /// comes from pictures (10 MB) need less than 64 MB. 192 MB is twice the need. It is a limit of the heap, not of
    /// the process: the whole child was seen at up to about 250 MB, beside an API of about 130 MB in the 512 MB of
    /// the instance. Should that ever not fit, the child is the process the kernel kills (see PdfWorkerHost).
    /// </summary>
    public const long DefaultHeapHardLimitBytes = 192L * 1024 * 1024;

    /// <summary>
    /// Time limit of one read, start of the process included. The reader inside the API had 30 s for a read that was
    /// already started and compiled; the child starts and compiles for every file, which on the 0.1 CPU of the
    /// production instance takes about 10 s by itself. Measured there, the largest statement the old reader read took
    /// 49 s through the child alone and up to 73 s inside the API while it answers one request per second; 120 s is
    /// about 1.6 times that, and still ends a read that is stuck long before the job is given up on (10 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Executable to start; null means the API executable itself, run as the PDF worker.</summary>
    public string? FileName { get; init; }

    /// <summary>Arguments for <see cref="FileName"/>; ignored when it is null.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public int MaxPages { get; init; } = PdfPigTextExtractor.DefaultMaxPages;

    /// <summary>Counts from just before the process is started, so the start-up time is inside it.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    public long HeapHardLimitBytes { get; init; } = DefaultHeapHardLimitBytes;

    /// <summary>When set, the child gets this directory as its temp directory instead of the parent one.</summary>
    public string? TempDirectory { get; init; }

    /// <summary>Called with the id of the child right after it started (used by the tests to check it is gone).</summary>
    public Action<int>? OnProcessStarted { get; init; }

    /// <summary>Replaces the kill of the process and its tree (used by the tests to make the kill itself fail).</summary>
    public Action<System.Diagnostics.Process>? KillProcess { get; init; }
}
