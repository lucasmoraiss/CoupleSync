using CoupleSync.Application.OcrImport;

namespace CoupleSync.UnitTests.OcrImport;

[Trait("Category", "Ocr")]
public sealed class FileTypeDetectorTests
{
    [Fact]
    public void DetectMimeType_JpegMagicBytes_ReturnsImageJpeg()
    {
        var header = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        Assert.Equal("image/jpeg", FileTypeDetector.DetectMimeType(header));
    }

    [Fact]
    public void DetectMimeType_PngMagicBytes_ReturnsImagePng()
    {
        var header = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.Equal("image/png", FileTypeDetector.DetectMimeType(header));
    }

    [Fact]
    public void DetectMimeType_PdfMagicBytes_ReturnsApplicationPdf()
    {
        var header = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 };
        Assert.Equal("application/pdf", FileTypeDetector.DetectMimeType(header));
    }

    [Fact]
    public void DetectMimeType_GifMagicBytes_ReturnsNull()
    {
        var header = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
        Assert.Null(FileTypeDetector.DetectMimeType(header));
    }

    [Fact]
    public void DetectMimeType_ExeMagicBytes_ReturnsNull()
    {
        var header = new byte[] { 0x4D, 0x5A, 0x90, 0x00 };
        Assert.Null(FileTypeDetector.DetectMimeType(header));
    }

    [Fact]
    public void DetectMimeType_EmptySpan_ReturnsNull()
    {
        Assert.Null(FileTypeDetector.DetectMimeType(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void DetectMimeType_TwoByteSpan_ReturnsNull()
    {
        var header = new byte[] { 0xFF, 0xD8 };
        Assert.Null(FileTypeDetector.DetectMimeType(header));
    }

    [Fact]
    public void DetectMimeType_ThreeByteJpeg_ReturnsImageJpeg()
    {
        var header = new byte[] { 0xFF, 0xD8, 0xFF };
        Assert.Equal("image/jpeg", FileTypeDetector.DetectMimeType(header));
    }
}
