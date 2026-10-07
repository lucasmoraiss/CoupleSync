using CoupleSync.Application.Common;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
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
    private readonly EmailCodeFlow _emailCodeFlow;
    private readonly ILogger<RegisterCommandHandler> _logger;

    public RegisterCommandHandler(
        IAuthRepository authRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenService jwtTokenService,
        ITokenHasher tokenHasher,
        IDateTimeProvider dateTimeProvider,
        IOptions<JwtOptions> jwtOptions,
        EmailCodeFlow emailCodeFlow,
        ILogger<RegisterCommandHandler> logger)
    {
        _authRepository = authRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _tokenHasher = tokenHasher;
        _dateTimeProvider = dateTimeProvider;
        _jwtOptions = jwtOptions.Value;
        _emailCodeFlow = emailCodeFlow;
        _logger = logger;
    }

    public async Task<AuthResult> HandleAsync(RegisterCommand command, CancellationToken cancellationToken)
    {
        var email = EmailAddress.From(command.Email).Value;

        if (await _authRepository.EmailExistsAsync(email, cancellationToken))
        {
            throw new ConflictException("EMAIL_ALREADY_IN_USE", "Já existe uma conta com esse e-mail.");
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
        catch (UniqueViolationException)
        {
            throw new ConflictException("EMAIL_ALREADY_IN_USE", "Já existe uma conta com esse e-mail.");
        }

        await SendVerificationCodeAsync(user, cancellationToken);

        return new AuthResult(
            new AuthenticatedUserDto(user.Id, user.Email, user.Name, user.EmailVerified),
            accessToken,
            refreshTokenRaw);
    }

    /// <summary>Best effort: the account is already saved, so no failure here (e-mail off, provider down, database hiccup) fails the sign-up.</summary>
    private async Task SendVerificationCodeAsync(User user, CancellationToken cancellationToken)
    {
        if (!_emailCodeFlow.IsEmailConfigured)
        {
            return;
        }

        try
        {
            await _emailCodeFlow.IssueAsync(user, EmailCodePurpose.EmailVerification, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not send the e-mail confirmation code to user {UserId}.", user.Id);
        }
    }

}
