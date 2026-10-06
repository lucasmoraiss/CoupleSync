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

    /// <summary>Deletes the refresh token with this hash. Returns false when none matched (already gone).</summary>
    Task<bool> RevokeRefreshTokenByHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Deletes every refresh token of the user (all sessions end). Returns how many were removed.</summary>
    Task<int> RevokeRefreshTokensByUserIdAsync(Guid userId, CancellationToken cancellationToken);

    Task<EmailCode?> FindEmailCodeAsync(Guid userId, string purpose, CancellationToken cancellationToken);

    /// <summary>Stores the code as the only live one for its user and purpose, dropping the previous one. Saves immediately.</summary>
    Task ReplaceEmailCodeAsync(EmailCode code, CancellationToken cancellationToken);

    /// <summary>
    /// Spends one verification attempt on the code, atomically. False when the attempts are already used up
    /// (the caller must then reject without comparing, even if the typed code is right).
    /// </summary>
    Task<bool> TryRegisterEmailCodeAttemptAsync(Guid codeId, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>Deletes the code. True for exactly one caller, so a code can be used only once even under concurrency.</summary>
    Task<bool> ConsumeEmailCodeAsync(Guid codeId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
