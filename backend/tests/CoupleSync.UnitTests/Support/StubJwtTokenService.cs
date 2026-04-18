using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class StubJwtTokenService : IJwtTokenService
{
    public string Token { get; set; } = "stub-access-token";

    public string GenerateAccessToken(User user)
    {
        return Token;
    }
}
