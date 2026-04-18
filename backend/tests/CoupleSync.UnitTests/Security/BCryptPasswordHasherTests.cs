using CoupleSync.Infrastructure.Security;

namespace CoupleSync.UnitTests.Security;

public sealed class BCryptPasswordHasherTests
{
    [Fact]
    public void HashAndVerify_ShouldRoundTripSuccessfully()
    {
        var hasher = new BCryptPasswordHasher();
        const string password = "MySecurePass123";

        var hash = hasher.HashPassword(password);

        Assert.NotEqual(password, hash);
        Assert.True(hasher.VerifyPassword(password, hash));
        Assert.False(hasher.VerifyPassword("wrong-password", hash));
    }
}
