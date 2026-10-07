using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.Auth;

public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword);

/// <summary>
/// Authenticated password change. Needs the current password. Every refresh token of the user is replaced by
/// a single new one, handed back to the caller: other devices can no longer renew their session, this one
/// stays signed in. (Access tokens already issued expire on their own within minutes.)
/// </summary>
public sealed class ChangePasswordCommandHandler
{
    private readonly IAuthRepository _authRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ITokenHasher _tokenHasher;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly JwtOptions _jwtOptions;

    public ChangePasswordCommandHandler(
        IAuthRepository authRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        ITokenHasher tokenHasher,
        IDateTimeProvider dateTimeProvider,
        IOptions<JwtOptions> jwtOptions)
    {
        _authRepository = authRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _tokenHasher = tokenHasher;
        _dateTimeProvider = dateTimeProvider;
        _jwtOptions = jwtOptions.Value;
    }

    public async Task<RefreshTokenResult> HandleAsync(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var user = await _authRepository.FindUserByIdAsync(command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        if (!_passwordHasher.VerifyPassword(command.CurrentPassword, user.PasswordHash))
        {
            // 400, not 401: the session is fine, only the typed password is wrong (a 401 would make the app drop the session).
            throw new BadRequestException(
                "INVALID_CURRENT_PASSWORD",
                "A senha atual está incorreta.",
                new Dictionary<string, string[]> { ["CurrentPassword"] = ["A senha atual está incorreta."] });
        }

        var problems = PasswordPolicy.Validate(command.NewPassword, user.Email).ToList();
        if (string.Equals(command.NewPassword, command.CurrentPassword, StringComparison.Ordinal))
        {
            problems.Add("A nova senha precisa ser diferente da atual.");
        }

        if (problems.Count > 0)
        {
            throw new BadRequestException(
                "VALIDATION_ERROR",
                problems[0],
                new Dictionary<string, string[]> { ["NewPassword"] = problems.ToArray() });
        }

        var now = _dateTimeProvider.UtcNow;
        user.ChangePasswordHash(_passwordHasher.HashPassword(command.NewPassword));

        var accessToken = _jwtTokenService.GenerateAccessToken(user);
        var refreshTokenRaw = RefreshTokenGenerator.Generate();
        var refreshToken = RefreshToken.CreateForUser(
            user.Id,
            _tokenHasher.Hash(refreshTokenRaw),
            now.AddDays(_jwtOptions.RefreshTokenTtlDays),
            now);

        await _authRepository.UpsertRefreshTokenAsync(refreshToken, cancellationToken);
        await _authRepository.SaveChangesAsync(cancellationToken);

        return new RefreshTokenResult(accessToken, refreshTokenRaw);
    }
}
