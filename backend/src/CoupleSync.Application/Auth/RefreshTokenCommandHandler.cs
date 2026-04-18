using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace CoupleSync.Application.Auth;

public sealed class RefreshTokenCommandHandler
{
    private readonly IAuthRepository _authRepository;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ITokenHasher _tokenHasher;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly JwtOptions _jwtOptions;

    public RefreshTokenCommandHandler(
        IAuthRepository authRepository,
        IJwtTokenService jwtTokenService,
        ITokenHasher tokenHasher,
        IDateTimeProvider dateTimeProvider,
        IOptions<JwtOptions> jwtOptions)
    {
        _authRepository = authRepository;
        _jwtTokenService = jwtTokenService;
        _tokenHasher = tokenHasher;
        _dateTimeProvider = dateTimeProvider;
        _jwtOptions = jwtOptions.Value;
    }

    public async Task<RefreshTokenResult> HandleAsync(RefreshTokenCommand command, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        var tokenHash = _tokenHasher.Hash(command.RefreshToken);
        var refreshToken = await _authRepository.FindRefreshTokenByHashAsync(tokenHash, cancellationToken);

        if (refreshToken is null)
        {
            ThrowInvalidSession();
        }

        if (refreshToken.ExpiresAtUtc <= now)
        {
            ThrowInvalidSession();
        }

        var user = await _authRepository.FindUserByIdAsync(refreshToken.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            ThrowInvalidSession();
        }

        var accessToken = _jwtTokenService.GenerateAccessToken(user);
        var newRefreshTokenRaw = RefreshTokenGenerator.Generate();
        var newRefreshTokenHash = _tokenHasher.Hash(newRefreshTokenRaw);
        var refreshTokenExpiresAt = now.AddDays(_jwtOptions.RefreshTokenTtlDays);

        var rotated = await _authRepository.RotateRefreshTokenIfMatchAsync(
            tokenHash,
            newRefreshTokenHash,
            refreshTokenExpiresAt,
            now,
            now,
            cancellationToken);

        if (!rotated)
        {
            ThrowInvalidSession();
        }

        return new RefreshTokenResult(accessToken, newRefreshTokenRaw);
    }

    [DoesNotReturn]
    private static void ThrowInvalidSession()
    {
        throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired session.");
    }
}
