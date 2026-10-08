using System.Text;
using CoupleSync.Application.Common.Exceptions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser;

public class PdfPigTextExtractor : IPdfTextExtractor
{
    /// <summary>A statement longer than this is not read (bank statements are a handful of pages).</summary>
    public const int DefaultMaxPages = 50;

    /// <summary>
    /// Time limit of this in-process reader when none is given; PdfPig is synchronous and cannot be cancelled mid-page.
    /// The API does not read PDFs here: it uses ChildProcessPdfTextExtractor (limit in PdfWorkerOptions), and the worker
    /// process builds this reader with no limit, because the parent kills it.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly int _maxPages;
    private readonly TimeSpan _timeout;

    public PdfPigTextExtractor() : this(DefaultMaxPages, DefaultTimeout)
    {
    }

    public PdfPigTextExtractor(int maxPages, TimeSpan timeout)
    {
        _maxPages = maxPages;
        _timeout = timeout;
    }

    protected virtual int MaxPages => _maxPages;

    protected virtual TimeSpan Timeout => _timeout;

    public string ExtractText(Stream pdfStream) => ExtractTextWithPageCount(pdfStream).Text;

    /// <summary>Same as <see cref="ExtractText"/>, also telling how many pages the document had.</summary>
    public PdfTextResult ExtractTextWithPageCount(Stream pdfStream)
    {
        // The work runs on a pool thread so that a PDF that never finishes parsing cannot hold the
        // caller (the import worker) past the time limit; the page loop also stops by itself.
        using var cancellation = new CancellationTokenSource();
        var worker = Task.Run(() => ReadAllPages(pdfStream, cancellation.Token));

        // WaitAny does not throw when the worker faulted; the error is rethrown unwrapped below.
        if (Task.WaitAny([worker], Timeout) < 0)
        {
            cancellation.Cancel();
            // This releases the caller, it does not stop PdfPig: a page that is mid-parse keeps its pool thread
            // until it returns (the loop stops at the next page boundary). Observe its error, nobody awaits it.
            worker.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new OcrException(
                "PDF_TIMEOUT",
                "A leitura do PDF demorou demais e foi interrompida. Envie um extrato menor ou exporte o PDF novamente.");
        }

        try
        {
            return worker.GetAwaiter().GetResult();
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new OcrException("PDF_ENCRYPTED", "O PDF está protegido por senha. Exporte sem senha e tente novamente.");
        }
    }

    private PdfTextResult ReadAllPages(Stream pdfStream, CancellationToken cancellation)
    {
        using var document = OpenDocument(pdfStream);

        if (document.NumberOfPages > MaxPages)
            throw new OcrException(
                "PDF_TOO_MANY_PAGES",
                $"O PDF tem {document.NumberOfPages} páginas; o limite é {MaxPages}. Envie apenas o período que deseja importar.");

        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            cancellation.ThrowIfCancellationRequested();
            sb.AppendLine(page.Text);
        }

        return new PdfTextResult(sb.ToString(), document.NumberOfPages);
    }

    protected virtual PdfDocument OpenDocument(Stream stream) => PdfDocument.Open(stream);
}

/// <summary>The text of a PDF and the number of pages it was read from.</summary>
public readonly record struct PdfTextResult(string Text, int Pages);
