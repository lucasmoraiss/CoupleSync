using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IJwtTokenService
{
    string GenerateAccessToken(User user);
}
