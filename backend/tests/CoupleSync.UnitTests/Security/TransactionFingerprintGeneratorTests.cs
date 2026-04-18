using CoupleSync.Infrastructure.Security;

namespace CoupleSync.UnitTests.Security;

public sealed class TransactionFingerprintGeneratorTests
{
    [Fact]
    public void Generate_SameInputs_ReturnsSameHash()
    {
        var coupleId = Guid.NewGuid();
        var timestamp = new DateTime(2026, 4, 14, 9, 0, 0, DateTimeKind.Utc);

        var hash1 = TransactionFingerprintGenerator.GenerateStatic(coupleId, "NUBANK", 99.99m, "BRL", timestamp, "IFOOD");
        var hash2 = TransactionFingerprintGenerator.GenerateStatic(coupleId, "NUBANK", 99.99m, "BRL", timestamp, "IFOOD");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Generate_DifferentAmount_ReturnsDifferentHash()
    {
        var coupleId = Guid.NewGuid();
        var timestamp = new DateTime(2026, 4, 14, 9, 0, 0, DateTimeKind.Utc);

        var hash1 = TransactionFingerprintGenerator.GenerateStatic(coupleId, "NUBANK", 99.99m, "BRL", timestamp, "IFOOD");
        var hash2 = TransactionFingerprintGenerator.GenerateStatic(coupleId, "NUBANK", 100.00m, "BRL", timestamp, "IFOOD");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Generate_DifferentCoupleId_ReturnsDifferentHash()
    {
        var timestamp = new DateTime(2026, 4, 14, 9, 0, 0, DateTimeKind.Utc);

        var hash1 = TransactionFingerprintGenerator.GenerateStatic(Guid.NewGuid(), "NUBANK", 99.99m, "BRL", timestamp, "IFOOD");
        var hash2 = TransactionFingerprintGenerator.GenerateStatic(Guid.NewGuid(), "NUBANK", 99.99m, "BRL", timestamp, "IFOOD");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Generate_CaseInsensitiveNormalization_ReturnsSameHash()
    {
        var coupleId = Guid.NewGuid();
        var timestamp = new DateTime(2026, 4, 14, 9, 0, 0, DateTimeKind.Utc);

        var hash1 = TransactionFingerprintGenerator.GenerateStatic(coupleId, "nubank", 99.99m, "brl", timestamp, "ifood");
        var hash2 = TransactionFingerprintGenerator.GenerateStatic(coupleId, "NUBANK", 99.99m, "BRL", timestamp, "IFOOD");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Generate_NullMerchant_ReturnsSameAsEmptyMerchant()
    {
        var coupleId = Guid.NewGuid();
        var timestamp = new DateTime(2026, 4, 14, 9, 0, 0, DateTimeKind.Utc);

        var hash1 = TransactionFingerprintGenerator.GenerateStatic(coupleId, "NUBANK", 99.99m, "BRL", timestamp, null);
        var hash2 = TransactionFingerprintGenerator.GenerateStatic(coupleId, "NUBANK", 99.99m, "BRL", timestamp, "");

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Generate_ReturnsValidSha256HexString()
    {
        var hash = TransactionFingerprintGenerator.GenerateStatic(Guid.NewGuid(), "NUBANK", 10m, "BRL",
            DateTime.UtcNow, "TEST");

        Assert.Equal(64, hash.Length); // SHA-256 = 32 bytes = 64 hex chars
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
