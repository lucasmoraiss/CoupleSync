using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.Auth;

public sealed class LoginCommandHandler
{
    private readonly IAuthRepository _authRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ITokenHasher _tokenHasher;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly JwtOptions _jwtOptions;

    public LoginCommandHandler(
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

    public async Task<AuthResult> HandleAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        var email = EmailAddress.From(command.Email).Value;
        var user = await _authRepository.FindUserByEmailAsync(email, cancellationToken);

        if (user is null || !_passwordHasher.VerifyPassword(command.Password, user.PasswordHash) || !user.IsActive)
        {
            throw new UnauthorizedException("INVALID_CREDENTIALS", "Invalid credentials.");
        }

        var now = _dateTimeProvider.UtcNow;
        var accessToken = _jwtTokenService.GenerateAccessToken(user);
        var refreshTokenRaw = RefreshTokenGenerator.Generate();
        var refreshTokenHash = _tokenHasher.Hash(refreshTokenRaw);
        var refreshToken = RefreshToken.CreateForUser(
            user.Id,
            refreshTokenHash,
            now.AddDays(_jwtOptions.RefreshTokenTtlDays),
            now);

        await _authRepository.UpsertRefreshTokenAsync(refreshToken, cancellationToken);
        await _authRepository.SaveChangesAsync(cancellationToken);

        return new AuthResult(
            new AuthenticatedUserDto(user.Id, user.Email, user.Name),
            accessToken,
            refreshTokenRaw);
    }
}
