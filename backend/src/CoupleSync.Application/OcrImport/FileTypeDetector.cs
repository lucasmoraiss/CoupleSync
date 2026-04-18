namespace CoupleSync.Application.OcrImport;

/// <summary>
/// Detects MIME type from file magic bytes. Does NOT trust the Content-Type header.
/// </summary>
public static class FileTypeDetector
{
    // JPEG: FF D8 FF
    // PNG:  89 50 4E 47
    // PDF:  25 50 44 46 (%PDF)
    public static string? DetectMimeType(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3
            && header[0] == 0xFF
            && header[1] == 0xD8
            && header[2] == 0xFF)
            return "image/jpeg";

        if (header.Length >= 4
            && header[0] == 0x89
            && header[1] == 0x50
            && header[2] == 0x4E
            && header[3] == 0x47)
            return "image/png";

        if (header.Length >= 4
            && header[0] == 0x25
            && header[1] == 0x50
            && header[2] == 0x44
            && header[3] == 0x46)
            return "application/pdf";

        return null;
    }
}
