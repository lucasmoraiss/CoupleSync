using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Support;

public static class TestJwtOptions
{
    public static IOptions<JwtOptions> Default() => Options.Create(new JwtOptions
    {
        Secret = "this-is-a-secure-test-secret-with-32chars",
        Issuer = "CoupleSync.Test",
        Audience = "CoupleSync.Mobile.Test",
        AccessTokenTtlMinutes = 15,
        RefreshTokenTtlDays = 7
    });
}
