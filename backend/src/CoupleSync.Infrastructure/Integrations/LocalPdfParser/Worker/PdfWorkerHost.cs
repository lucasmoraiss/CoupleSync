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

    /// <summary>The file could not be opened as a PDF (corrupt or not a PDF at all).</summary>
    public const string UnreadableCode = "PDF_UNREADABLE";

    /// <summary>The worker process died or answered nothing (set by the parent, never by the worker).</summary>
    public const string WorkerFailedCode = "PDF_WORKER_FAILED";

    internal static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static int Run(string[] args, Stream input, Stream output)
    {
        var maxPages = ParseMaxPages(args);
        WorkerAnswer answer;
        try
        {
            using var pdf = new MemoryStream();
            input.CopyTo(pdf);
            pdf.Position = 0;

            // The parent owns the time limit and kills this process when it is over; no second timer here.
            var result = new PdfPigTextExtractor(maxPages, System.Threading.Timeout.InfiniteTimeSpan).ExtractTextWithPageCount(pdf);
            answer = new WorkerAnswer(true, result.Pages, result.Text, null, null);
        }
        catch (OcrException ex)
        {
            answer = new WorkerAnswer(false, null, null, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Out of memory is not a verdict on the file: it ends the process (the parent reports a failed worker).
            // The exception text may quote the document, so only a fixed message leaves this process.
            answer = new WorkerAnswer(
                false, null, null, UnreadableCode,
                "Não foi possível ler o PDF. O arquivo parece corrompido; exporte o extrato novamente.");
        }

        JsonSerializer.Serialize(output, answer, Json);
        output.Flush();
        return 0;
    }

    private static int ParseMaxPages(string[] args)
    {
        var index = Array.IndexOf(args, MaxPagesArgument);
        return index >= 0 && index + 1 < args.Length
               && int.TryParse(args[index + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
               && value > 0
            ? value
            : PdfPigTextExtractor.DefaultMaxPages;
    }
}

internal sealed record WorkerAnswer(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("pages")] int? Pages,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message);
