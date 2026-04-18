using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.OcrImport;

public sealed class OcrCandidate
{
    public int Index { get; set; }
    public DateTime Date { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "BRL";
    public double Confidence { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public bool DuplicateSuspected { get; set; }
    /// <summary>Credit or Debit — populated by local-pdf parser; null when source is Azure OCR.</summary>
    public TransactionType? Type { get; set; }
    /// <summary>AI-suggested category; null when classifier was unavailable or returned no match.</summary>
    public string? SuggestedCategory { get; set; }
}
