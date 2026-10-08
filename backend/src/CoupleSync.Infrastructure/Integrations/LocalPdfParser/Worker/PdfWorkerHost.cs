using System.Text.Json;
using System.Text.Json.Serialization;
using CoupleSync.Application.Common.Exceptions;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker;

/// <summary>
/// The child side of the PDF reading: the API executable started with <see cref="Command"/> reads one PDF from
/// the standard input and writes one JSON answer to the standard output, then ends. It builds no web host, opens
/// no database and logs nothing: whatever it prints is the answer, and no PDF content ever goes anywhere else.
/// </summary>
public static class PdfWorkerHost
{
    /// <summary>First argument that makes the API executable behave as the PDF worker.</summary>
    public const string Command = "--pdf-worker";

    public const string MaxPagesArgument = "--max-pages";

    /// <summary>Size of the PDF that is coming, so that it is held once, in a buffer of that size.</summary>
    public const string InputBytesArgument = "--input-bytes";

    /// <summary>A size hint above this is ignored (the upload limit is far below it).</summary>
    private const int MaxInputBytesHint = 64 * 1024 * 1024;

    /// <summary>The file could not be opened as a PDF (corrupt or not a PDF at all).</summary>
    public const string UnreadableCode = "PDF_UNREADABLE";

    /// <summary>The worker process died or answered nothing (set by the parent, never by the worker).</summary>
    public const string WorkerFailedCode = "PDF_WORKER_FAILED";

    internal static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static int Run(string[] args, Stream input, Stream output)
    {
        var maxPages = ParsePositive(args, MaxPagesArgument) ?? PdfPigTextExtractor.DefaultMaxPages;
        var inputBytes = ParsePositive(args, InputBytesArgument);
        WorkerAnswer answer;
        try
        {
            // The standard input cannot be read backwards and PdfPig needs that, so the PDF is held in memory: once,
            // in a buffer of its own size when the parent told it (a buffer that grows by doubling holds up to twice that).
            using var pdf = inputBytes is > 0 and <= MaxInputBytesHint ? new MemoryStream(inputBytes.Value) : new MemoryStream();
            input.CopyTo(pdf);
            pdf.Position = 0;

            // The parent owns the time limit and kills this process when it is over; no second timer here.
            var result = new PdfPigTextExtractor(maxPages, System.Threading.Timeout.InfiniteTimeSpan).ExtractTextWithPageCount(pdf);
            answer = new WorkerAnswer(true, result.Pages, result.Text, null, null, null);
        }
        catch (OcrException ex)
        {
            answer = new WorkerAnswer(false, null, null, ex.Code, ex.Message, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Out of memory is not a verdict on the file: it ends the process (the parent reports a failed worker).
            // The exception text may quote the document, so only a fixed message leaves this process, with the name of
            // the exception type (never its message): it is what tells a broken file from a defect of the library.
            answer = new WorkerAnswer(
                false, null, null, UnreadableCode,
                "Não foi possível ler o PDF. O arquivo parece corrompido; exporte o extrato novamente.",
                ex.GetType().Name);
        }

        JsonSerializer.Serialize(output, answer, Json);
        output.Flush();
        return 0;
    }

    private static int? ParsePositive(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length
               && int.TryParse(args[index + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
               && value > 0
            ? value
            : null;
    }
}

internal sealed record WorkerAnswer(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("pages")] int? Pages,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("errorType")] string? ErrorType);
