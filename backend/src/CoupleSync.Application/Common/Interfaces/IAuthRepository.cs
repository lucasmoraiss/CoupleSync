using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IAuthRepository
{
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    Task<User?> FindUserByEmailAsync(string email, CancellationToken cancellationToken);

    Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    Task<RefreshToken?> FindRefreshTokenByHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<RefreshToken?> FindRefreshTokenByUserIdAsync(Guid userId, CancellationToken cancellationToken);

    Task AddUserAsync(User user, CancellationToken cancellationToken);

    Task UpsertRefreshTokenAsync(RefreshToken refreshToken, CancellationToken cancellationToken);

    Task<bool> RotateRefreshTokenIfMatchAsync(
        string currentTokenHash,
        string newTokenHash,
        DateTime newExpiresAtUtc,
        DateTime updatedAtUtc,
        DateTime now,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
