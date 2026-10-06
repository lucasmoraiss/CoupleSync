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

    /// <summary>The live code of the user for the purpose, tracked so it can be re-issued in place; null when none.</summary>
    Task<EmailCode?> FindEmailCodeForUpdateAsync(Guid userId, string purpose, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a new code, or the changes made to one returned by <see cref="FindEmailCodeForUpdateAsync"/>. Throws
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> when a concurrent request inserted the same
    /// (user, purpose) first; the failed entity is detached so the caller can read again and retry.
    /// </summary>
    Task StoreEmailCodeAsync(EmailCode code, CancellationToken cancellationToken);

    /// <summary>
    /// Spends one verification attempt on the code, atomically. False when the attempts are already used up
    /// (the caller must then reject without comparing, even if the typed code is right).
    /// </summary>
    Task<bool> TryRegisterEmailCodeAttemptAsync(Guid codeId, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>Deletes the code. True for exactly one caller, so a code can be used only once even under concurrency.</summary>
    Task<bool> ConsumeEmailCodeAsync(Guid codeId, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the action in one database transaction: everything it saves, deletes or updates is committed together, or
    /// (when it throws) rolled back together. Joins the current transaction if there already is one.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<Task> action, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
