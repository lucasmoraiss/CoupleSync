namespace CoupleSync.Application.Common.Interfaces;

public interface IFingerprintGenerator
{
    string Generate(Guid coupleId, string bank, decimal amount, string currency, DateTime eventTimestamp, string? merchant);
}
