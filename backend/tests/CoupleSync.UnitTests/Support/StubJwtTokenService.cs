using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class StubJwtTokenService : IJwtTokenService
{
    public string Token { get; set; } = "stub-access-token";

    /// <summary>The active group of the user at each moment a token was issued (what its couple_id claim would be).</summary>
    public List<Guid?> IssuedForCoupleIds { get; } = new();

    public string GenerateAccessToken(User user)
    {
        IssuedForCoupleIds.Add(user.ActiveCoupleId);
        return Token;
    }
}
