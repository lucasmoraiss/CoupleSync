using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public enum EmailCodeReissueResult
{
    Reissued,
    LimitReached,
    NoCode
}

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

    /// <summary>
    /// Re-issues the existing code of the user and purpose in ONE conditional statement (a single UPDATE ... WHERE the
    /// window has expired OR issue_count &lt; max): the check and the increment cannot interleave with another request, so
    /// across any number of concurrent calls at most <paramref name="maxPerWindow"/> succeed per window. Attempts start over.
    /// </summary>
    Task<EmailCodeReissueResult> TryReissueEmailCodeAsync(
        Guid userId,
        string purpose,
        string codeHash,
        DateTime expiresAtUtc,
        DateTime now,
        int maxPerWindow,
        TimeSpan window,
        CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the first code of a user and purpose. Throws <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>
    /// when a concurrent request inserted it first (unique index); the failed entity is detached so the caller can retry.
    /// </summary>
    Task AddEmailCodeAsync(EmailCode code, CancellationToken cancellationToken);

    /// <summary>
    /// Spends one verification attempt on the code, atomically. False when the attempts are already used up
    /// (the caller must then reject without comparing, even if the typed code is right).
    /// </summary>
    Task<bool> TryRegisterEmailCodeAttemptAsync(Guid codeId, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the code only if it still holds the hash that was verified (a code re-issued in the meantime keeps the row id
    /// but not the hash). True for exactly one caller, so a code can be used only once even under concurrency.
    /// </summary>
    Task<bool> ConsumeEmailCodeAsync(Guid codeId, string verifiedCodeHash, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the action in one database transaction: everything it saves, deletes or updates is committed together, or
    /// (when it throws) rolled back together. Joins the current transaction if there already is one.
    /// </summary>
    Task ExecuteInTransactionAsync(Func<Task> action, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
