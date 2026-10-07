using System.Text;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>Invented PDFs built byte by byte (no real statement, no personal data), used to feed the PDF worker.</summary>
internal static class SyntheticPdf
{
    /// <summary>Minimal valid PDF with one text line per page (Latin-1 so string length equals byte count).</summary>
    public static byte[] Pages(int pageCount, bool encrypted = false, int linesPerPage = 1)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();

        // Objects: 1 catalog, 2 pages, 3 font, then (page, content) pairs, then the encryption dictionary.
        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<</Type /Catalog /Pages 2 0 R>>\nendobj\n");

        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{4 + i * 2} 0 R"));
        offsets.Add(sb.Length);
        sb.Append($"2 0 obj\n<</Type /Pages /Kids [{kids}] /Count {pageCount}>>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append("3 0 obj\n<</Type /Font /Subtype /Type1 /BaseFont /Helvetica>>\nendobj\n");

        for (var i = 0; i < pageCount; i++)
        {
            var pageObj = 4 + i * 2;
            var contentObj = pageObj + 1;
            var content = $"BT /F1 12 Tf 14 TL 20 100 Td (Pagina {i + 1} do extrato) Tj"
                + string.Concat(Enumerable.Range(0, linesPerPage - 1).Select(l => $" (Linha {l} da pagina {i + 1} com texto de enchimento) '"))
                + " ET\n";

            offsets.Add(sb.Length);
            sb.Append($"{pageObj} 0 obj\n<</Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents {contentObj} 0 R /Resources <</Font <</F1 3 0 R>>>>>>\nendobj\n");
            offsets.Add(sb.Length);
            sb.Append($"{contentObj} 0 obj\n<</Length {content.Length}>>\nstream\n{content}endstream\nendobj\n");
        }

        var trailerExtra = "";
        if (encrypted)
        {
            // A standard security handler (RC4, 40 bit) whose user password is not empty: nothing here can open it.
            var encryptObj = 4 + pageCount * 2;
            offsets.Add(sb.Length);
            sb.Append($"{encryptObj} 0 obj\n<</Filter /Standard /V 1 /R 2 /P -44 /O <{new string('A', 64)}> /U <{new string('B', 64)}>>>\nendobj\n");
            trailerExtra = $" /Encrypt {encryptObj} 0 R /ID [<{new string('C', 32)}> <{new string('C', 32)}>]";
        }

        var xref = sb.Length;
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<</Size {offsets.Count + 1} /Root 1 0 R{trailerExtra}>>\nstartxref\n{xref}\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
