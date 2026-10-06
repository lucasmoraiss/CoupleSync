using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Application.Auth;

/// <summary>
/// The shared rules of the e-mailed codes (e-mail confirmation and password reset): issue a code (replacing
/// the previous one of the same purpose) and e-mail it, and verify a typed code (counted attempts, expiry,
/// constant-time comparison, single use).
/// </summary>
public sealed class EmailCodeFlow
{
    public const int CodeValidMinutes = 15;
    public const int MaxAttempts = 5;

    public const string InvalidCodeMessage = "Código inválido ou expirado. Solicite um novo código.";

    private readonly IAuthRepository _authRepository;
    private readonly IVerificationCodeService _codes;
    private readonly IEmailSender _emailSender;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly CodeRequestThrottle _throttle;
    private readonly ILogger<EmailCodeFlow> _logger;

    public EmailCodeFlow(
        IAuthRepository authRepository,
        IVerificationCodeService codes,
        IEmailSender emailSender,
        IDateTimeProvider dateTimeProvider,
        CodeRequestThrottle throttle,
        ILogger<EmailCodeFlow> logger)
    {
        _authRepository = authRepository;
        _codes = codes;
        _emailSender = emailSender;
        _dateTimeProvider = dateTimeProvider;
        _throttle = throttle;
        _logger = logger;
    }

    public bool IsEmailConfigured => _emailSender.IsConfigured;

    /// <summary>The 503 every e-mail route answers while no provider is configured.</summary>
    public void EnsureEmailConfigured()
    {
        if (!_emailSender.IsConfigured)
        {
            throw new AppException(
                "EMAIL_NOT_CONFIGURED",
                "O envio de e-mail não está disponível no momento. Tente novamente mais tarde.",
                503);
        }
    }

    /// <summary>
    /// Creates a new code, invalidating the previous one, and e-mails it. False when nothing was sent because the
    /// address hit its request cap or a concurrent request already issued one. A provider failure is logged and
    /// swallowed: callers answer the same way whether or not the e-mail went out.
    /// </summary>
    public async Task<bool> IssueAsync(User user, string purpose, CancellationToken cancellationToken)
    {
        if (!_throttle.TryAcquire(purpose, user.Email))
        {
            _logger.LogInformation("Code request for user {UserId} ({Purpose}) skipped: request cap reached.", user.Id, purpose);
            return false;
        }

        var now = _dateTimeProvider.UtcNow;
        var code = _codes.Generate();
        var entity = EmailCode.Create(user.Id, purpose, _codes.Hash(user.Id, purpose, code), now.AddMinutes(CodeValidMinutes), now);

        try
        {
            await _authRepository.ReplaceEmailCodeAsync(entity, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Two requests for the same user raced on the unique (user, purpose) index; the other one's code is the live one.
            _logger.LogWarning(ex, "Could not store the {Purpose} code for user {UserId}.", purpose, user.Id);
            return false;
        }

        var message = purpose == EmailCodePurpose.PasswordReset
            ? EmailTemplates.PasswordReset(user.Email, user.Name, code, CodeValidMinutes)
            : EmailTemplates.EmailVerification(user.Email, user.Name, code, CodeValidMinutes);

        try
        {
            await _emailSender.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sending the {Purpose} e-mail for user {UserId} failed.", purpose, user.Id);
        }

        return true;
    }

    /// <summary>
    /// Checks a typed code and consumes it. Every failure (unknown user, no code, expired, attempts used up, wrong
    /// code) throws the same INVALID_CODE error and spends comparable work, so the answer reveals nothing about the account.
    /// </summary>
    public async Task VerifyAndConsumeAsync(User? user, string purpose, string code, CancellationToken cancellationToken)
    {
        var typed = (code ?? string.Empty).Trim();

        if (user is null)
        {
            _codes.Verify(Guid.Empty, purpose, typed, _codes.Hash(Guid.Empty, purpose, "000000"));
            throw InvalidCode();
        }

        var stored = await _authRepository.FindEmailCodeAsync(user.Id, purpose, cancellationToken);
        if (stored is null || stored.IsExpired(_dateTimeProvider.UtcNow))
        {
            _codes.Verify(user.Id, purpose, typed, _codes.Hash(user.Id, purpose, "000000"));
            throw InvalidCode();
        }

        // Spent before comparing, atomically: parallel guesses cannot exceed the limit, and the 6th try fails even with the right code.
        if (!await _authRepository.TryRegisterEmailCodeAttemptAsync(stored.Id, MaxAttempts, cancellationToken))
        {
            throw InvalidCode();
        }

        if (!_codes.Verify(user.Id, purpose, typed, stored.CodeHash))
        {
            throw InvalidCode();
        }

        if (!await _authRepository.ConsumeEmailCodeAsync(stored.Id, cancellationToken))
        {
            throw InvalidCode();
        }
    }

    private static BadRequestException InvalidCode() =>
        new("INVALID_CODE", InvalidCodeMessage, new Dictionary<string, string[]> { ["Code"] = [InvalidCodeMessage] });
}
