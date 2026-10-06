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
public enum CodeIssueOutcome
{
    Sent,
    Throttled,
    Raced
}

public sealed class EmailCodeFlow
{
    public const int CodeValidMinutes = 15;
    public const int MaxAttempts = 5;
    public const int MaxCodesPerWindow = 5;
    public static readonly TimeSpan IssueWindow = TimeSpan.FromHours(1);

    public const string InvalidCodeMessage = "Código inválido ou expirado. Solicite um novo código.";

    private readonly IAuthRepository _authRepository;
    private readonly IVerificationCodeService _codes;
    private readonly IEmailSender _emailSender;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<EmailCodeFlow> _logger;

    public EmailCodeFlow(
        IAuthRepository authRepository,
        IVerificationCodeService codes,
        IEmailSender emailSender,
        IDateTimeProvider dateTimeProvider,
        ILogger<EmailCodeFlow> logger)
    {
        _authRepository = authRepository;
        _codes = codes;
        _emailSender = emailSender;
        _dateTimeProvider = dateTimeProvider;
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
    /// Creates a new code (invalidating the previous one) and e-mails it. The cap (<see cref="MaxCodesPerWindow"/> per
    /// hour per user and purpose) is kept in the database next to the code and enforced by one atomic conditional UPDATE,
    /// so it holds across restarts and across any number of concurrent requests; only a request whose write won is e-mailed.
    /// A provider failure is logged and swallowed. Any other storage failure propagates (see <see cref="TryIssueAsync"/>).
    /// </summary>
    public async Task<CodeIssueOutcome> IssueAsync(User user, string purpose, CancellationToken cancellationToken)
    {
        // Two first-ever requests can race on the unique (user, purpose) index: the loser retries through the UPDATE path.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var now = _dateTimeProvider.UtcNow;
            var code = _codes.Generate();
            var hash = _codes.Hash(user.Id, purpose, code);
            var expires = now.AddMinutes(CodeValidMinutes);

            var reissue = await _authRepository.TryReissueEmailCodeAsync(
                user.Id, purpose, hash, expires, now, MaxCodesPerWindow, IssueWindow, cancellationToken);

            if (reissue == EmailCodeReissueResult.LimitReached)
            {
                _logger.LogInformation("Code request for user {UserId} ({Purpose}) skipped: request cap reached.", user.Id, purpose);
                return CodeIssueOutcome.Throttled;
            }

            if (reissue == EmailCodeReissueResult.NoCode)
            {
                try
                {
                    await _authRepository.AddEmailCodeAsync(EmailCode.Create(user.Id, purpose, hash, expires, now), cancellationToken);
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    _logger.LogInformation("Lost the insert race for the {Purpose} code of user {UserId} (attempt {Attempt}).", purpose, user.Id, attempt);
                    continue;
                }
            }

            await SendAsync(user, purpose, code, cancellationToken);
            return CodeIssueOutcome.Sent;
        }

        // Another request inserted the first code at the same moment and its e-mail was queued by that request.
        return CodeIssueOutcome.Raced;
    }

    /// <summary>
    /// For routes that must answer the same whatever happens (password reset request): any failure is logged, never thrown.
    /// </summary>
    public async Task TryIssueAsync(User user, string purpose, CancellationToken cancellationToken)
    {
        try
        {
            await IssueAsync(user, purpose, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Issuing the {Purpose} code for user {UserId} failed.", purpose, user.Id);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23505", StringComparison.OrdinalIgnoreCase)
            || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SendAsync(User user, string purpose, string code, CancellationToken cancellationToken)
    {
        var message = purpose == EmailCodePurpose.PasswordReset
            ? EmailTemplates.PasswordReset(user.Email, code, CodeValidMinutes)
            : EmailTemplates.EmailVerification(user.Email, code, CodeValidMinutes);

        try
        {
            await _emailSender.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Sending the {Purpose} e-mail for user {UserId} failed.", purpose, user.Id);
        }
    }

    /// <summary>
    /// Checks a typed code and spends one attempt (counted before comparing, atomically). Every failure (unknown user, no
    /// code, expired, attempts used up, wrong code) throws the same INVALID_CODE error. An unknown user or missing code still
    /// does one comparison, which only narrows the timing difference with a real code. Returns the code so the caller can
    /// consume it together with whatever it unlocks (see <see cref="ConsumeAsync"/>).
    /// </summary>
    public async Task<EmailCode> VerifyAsync(User? user, string purpose, string code, CancellationToken cancellationToken)
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

        return stored;
    }

    /// <summary>
    /// Deletes a verified code; succeeds for exactly one caller, so a code is single use even under concurrency. Call it
    /// inside the same transaction as the change it unlocks, so a failure rolls the consumption back too.
    /// </summary>
    public async Task ConsumeAsync(EmailCode code, CancellationToken cancellationToken)
    {
        if (!await _authRepository.ConsumeEmailCodeAsync(code.Id, code.CodeHash, cancellationToken))
        {
            throw InvalidCode();
        }
    }

    private static BadRequestException InvalidCode() =>
        new("INVALID_CODE", InvalidCodeMessage, new Dictionary<string, string[]> { ["Code"] = [InvalidCodeMessage] });
}
