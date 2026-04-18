using System.IdentityModel.Tokens.Jwt;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Security;

public sealed class JwtTokenServiceTests
{
    [Fact]
    public void GenerateAccessToken_ShouldContainExpectedClaims()
    {
        var options = Options.Create(new JwtOptions
        {
            Secret = "this-is-a-secure-test-secret-with-32chars",
            Issuer = "CoupleSync.Test",
            Audience = "CoupleSync.Mobile.Test",
            AccessTokenTtlMinutes = 15,
            RefreshTokenTtlDays = 7
        });

        var user = User.Create(
            EmailAddress.From("user@example.com"),
            "Test User",
            "password-hash",
            DateTime.UtcNow);

        var service = new JwtTokenService(options);
        var token = service.GenerateAccessToken(user);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(user.Id.ToString(), jwt.Claims.Single(x => x.Type == "sub").Value);
        Assert.Equal(user.Id.ToString(), jwt.Claims.Single(x => x.Type == "user_id").Value);
        Assert.Equal(string.Empty, jwt.Claims.Single(x => x.Type == "couple_id").Value);
        Assert.Equal("[]", jwt.Claims.Single(x => x.Type == "roles").Value);
        Assert.Equal(user.Name, jwt.Claims.Single(x => x.Type == "name").Value);
        Assert.Equal(user.Email, jwt.Claims.Single(x => x.Type == "email").Value);
    }
}
