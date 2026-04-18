using System.Text;
using CoupleSync.Application.Common.Exceptions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace CoupleSync.Infrastructure.Integrations.LocalPdfParser;

public class PdfPigTextExtractor : IPdfTextExtractor
{
    public string ExtractText(Stream pdfStream)
    {
        try
        {
            using var document = OpenDocument(pdfStream);
            var sb = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
            }
            return sb.ToString();
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new OcrException("PDF_ENCRYPTED", "O PDF está protegido por senha. Exporte sem senha e tente novamente.");
        }
    }

    protected virtual PdfDocument OpenDocument(Stream stream) => PdfDocument.Open(stream);
}
