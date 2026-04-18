namespace CoupleSync.Application.Common.Exceptions;

public sealed class OcrQuotaExhaustedException : Exception
{
    public DateTime? QuotaResetDate { get; }

    public OcrQuotaExhaustedException(string message, DateTime? quotaResetDate = null)
        : base(message)
    {
        QuotaResetDate = quotaResetDate;
    }
}
