using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Auth;

public static class RefreshTokenIssuer
{
    /// <summary>
    /// For routes that already return an access token (create/join group): when the user has no usable refresh
    /// token (it was deleted when they were removed from a group, or it expired), stores a new one and returns
    /// the raw value so the session keeps renewing. When a valid one exists it is left alone and null is
    /// returned, so an installed app that never reads this field keeps its working token. Not saved here:
    /// the caller's SaveChanges commits it together with its own change.
    /// </summary>
    public static async Task<string?> EnsureAsync(
        IAuthRepository authRepository,
        ITokenHasher tokenHasher,
        Guid userId,
        DateTime now,
        int ttlDays,
        CancellationToken cancellationToken)
    {
        var existing = await authRepository.FindRefreshTokenByUserIdAsync(userId, cancellationToken);
        if (existing is not null && existing.ExpiresAtUtc > now)
        {
            return null;
        }

        var raw = RefreshTokenGenerator.Generate();
        await authRepository.UpsertRefreshTokenAsync(
            RefreshToken.CreateForUser(userId, tokenHasher.Hash(raw), now.AddDays(ttlDays), now),
            cancellationToken);
        return raw;
    }
}
