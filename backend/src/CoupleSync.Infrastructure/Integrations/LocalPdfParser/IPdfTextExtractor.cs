namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser;

public interface IPdfTextExtractor
{
    /// <summary>
    /// Extracts all text from the given PDF stream.
    /// Returns text as-is even if shorter than 50 characters (caller decides EC-002).
    /// </summary>
    /// <exception cref="CoupleSync.Application.Common.Exceptions.OcrException">
    /// Thrown with code PDF_ENCRYPTED when the PDF is password-protected (EC-001).
    /// </exception>
    string ExtractText(Stream pdfStream);
}
