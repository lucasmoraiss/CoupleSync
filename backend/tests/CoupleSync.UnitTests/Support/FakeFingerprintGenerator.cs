using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Infrastructure.Security;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeFingerprintGenerator : IFingerprintGenerator
{
    public string Generate(Guid coupleId, string bank, decimal amount,
        string currency, DateTime eventTimestamp, string? merchant)
    {
        return TransactionFingerprintGenerator.GenerateStatic(coupleId, bank, amount, currency, eventTimestamp, merchant);
    }
}
