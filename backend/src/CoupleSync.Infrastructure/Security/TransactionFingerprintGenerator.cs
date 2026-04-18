using System.Security.Cryptography;
using System.Text;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Infrastructure.Security;

public sealed class TransactionFingerprintGenerator : IFingerprintGenerator
{
    public string Generate(Guid coupleId, string bank, decimal amount,
        string currency, DateTime eventTimestamp, string? merchant)
    {
        return GenerateStatic(coupleId, bank, amount, currency, eventTimestamp, merchant);
    }

    public static string GenerateStatic(Guid coupleId, string bank, decimal amount,
        string currency, DateTime eventTimestamp, string? merchant)
    {
        var normalized = $"{coupleId}|{bank.Trim().ToUpperInvariant()}|{amount:F2}|{currency.Trim().ToUpperInvariant()}|{eventTimestamp:O}|{(merchant ?? "").Trim().ToUpperInvariant()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
