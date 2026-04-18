using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.Auth;

public sealed class RegisterCommandHandler
{
    private readonly IAuthRepository _authRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly ITokenHasher _tokenHasher;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly JwtOptions _jwtOptions;

    public RegisterCommandHandler(
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

    public async Task<AuthResult> HandleAsync(RegisterCommand command, CancellationToken cancellationToken)
    {
        var email = EmailAddress.From(command.Email).Value;

        if (await _authRepository.EmailExistsAsync(email, cancellationToken))
        {
            throw new ConflictException("EMAIL_ALREADY_IN_USE", "Email is already in use.");
        }

        var now = _dateTimeProvider.UtcNow;
        var user = User.Create(EmailAddress.From(command.Email), command.Name, _passwordHasher.HashPassword(command.Password), now);

        await _authRepository.AddUserAsync(user, cancellationToken);

        var accessToken = _jwtTokenService.GenerateAccessToken(user);
        var refreshTokenRaw = RefreshTokenGenerator.Generate();
        var refreshTokenHash = _tokenHasher.Hash(refreshTokenRaw);
        var refreshToken = RefreshToken.CreateForUser(
            user.Id,
            refreshTokenHash,
            now.AddDays(_jwtOptions.RefreshTokenTtlDays),
            now);

        await _authRepository.UpsertRefreshTokenAsync(refreshToken, cancellationToken);
        try
        {
            await _authRepository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new ConflictException("EMAIL_ALREADY_IN_USE", "Email already in use.");
        }

        return new AuthResult(
            new AuthenticatedUserDto(user.Id, user.Email, user.Name),
            accessToken,
            refreshTokenRaw);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23505", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }
}
