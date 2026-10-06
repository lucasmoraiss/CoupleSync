using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Couples;

internal static class SessionTokens
{
    /// <summary>
    /// A new access token for the user's current active group plus a new refresh token that replaces the
    /// previous one (which stops working). Not saved here: the caller's SaveChanges commits it with its change.
    /// </summary>
    public static async Task<(string AccessToken, string RefreshToken)> ReplaceAsync(
        User user,
        IJwtTokenService jwtTokenService,
        IAuthRepository authRepository,
        ITokenHasher tokenHasher,
        DateTime now,
        int refreshTokenTtlDays,
        CancellationToken cancellationToken)
    {
        var accessToken = jwtTokenService.GenerateAccessToken(user);
        var refreshTokenRaw = RefreshTokenGenerator.Generate();
        await authRepository.UpsertRefreshTokenAsync(
            RefreshToken.CreateForUser(user.Id, tokenHasher.Hash(refreshTokenRaw), now.AddDays(refreshTokenTtlDays), now),
            cancellationToken);
        return (accessToken, refreshTokenRaw);
    }
}

internal static class GroupLimit
{
    public static void EnsureRoomForOneMore(int currentGroupCount)
    {
        if (currentGroupCount >= User.MaxGroups)
        {
            throw new ConflictException(
                "GROUP_LIMIT_REACHED",
                $"Você já participa de {User.MaxGroups} grupos, que é o máximo. Saia de um deles para entrar em outro.");
        }
    }
}
