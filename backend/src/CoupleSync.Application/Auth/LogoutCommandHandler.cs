using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Auth;

public sealed record LogoutCommand(string RefreshToken, string? DeviceToken = null);

/// <summary>
/// Ends the session on the server: the refresh token stops working. There is one refresh token per user, so
/// this is also what "sign out of every device" means; no separate action exists. Idempotent: an unknown or
/// already-revoked token is not an error (the caller is leaving either way, and the answer must not reveal
/// whether a token was valid). Access tokens already issued live out their short lifetime.
/// When the request carries this device's push token and it is registered to the user whose refresh token is
/// being revoked, that registration is deleted so the phone stops receiving the signed-out user's pushes.
/// A device token that belongs to someone else, or comes with an unknown refresh token, is ignored.
/// </summary>
public sealed class LogoutCommandHandler
{
    private readonly IAuthRepository _authRepository;
    private readonly ITokenHasher _tokenHasher;
    private readonly IDeviceTokenRepository _deviceTokenRepository;

    public LogoutCommandHandler(
        IAuthRepository authRepository,
        ITokenHasher tokenHasher,
        IDeviceTokenRepository deviceTokenRepository)
    {
        _authRepository = authRepository;
        _tokenHasher = tokenHasher;
        _deviceTokenRepository = deviceTokenRepository;
    }

    public async Task HandleAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        var tokenHash = _tokenHasher.Hash(command.RefreshToken);

        if (!string.IsNullOrWhiteSpace(command.DeviceToken))
        {
            var refreshToken = await _authRepository.FindRefreshTokenByHashAsync(tokenHash, cancellationToken);
            if (refreshToken is not null)
            {
                await _deviceTokenRepository.DeleteForUserAsync(refreshToken.UserId, command.DeviceToken, cancellationToken);
            }
        }

        await _authRepository.RevokeRefreshTokenByHashAsync(tokenHash, cancellationToken);
    }
}
