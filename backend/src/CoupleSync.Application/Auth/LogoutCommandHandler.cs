using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Auth;

public sealed record LogoutCommand(string RefreshToken);

/// <summary>
/// Ends the session on the server: the refresh token stops working. There is one refresh token per user, so
/// this is also what "sign out of every device" means; no separate action exists. Idempotent: an unknown or
/// already-revoked token is not an error (the caller is leaving either way, and the answer must not reveal
/// whether a token was valid). Access tokens already issued live out their short lifetime.
/// </summary>
public sealed class LogoutCommandHandler
{
    private readonly IAuthRepository _authRepository;
    private readonly ITokenHasher _tokenHasher;

    public LogoutCommandHandler(IAuthRepository authRepository, ITokenHasher tokenHasher)
    {
        _authRepository = authRepository;
        _tokenHasher = tokenHasher;
    }

    public async Task HandleAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        await _authRepository.RevokeRefreshTokenByHashAsync(_tokenHasher.Hash(command.RefreshToken), cancellationToken);
    }
}
