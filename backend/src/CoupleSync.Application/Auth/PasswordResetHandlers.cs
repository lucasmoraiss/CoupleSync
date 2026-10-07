using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.Auth;

public sealed record RequestPasswordResetCommand(string Email);

public sealed record ResetPasswordCommand(string Email, string Code, string NewPassword);

/// <summary>
/// Asks for a password-reset code. Answers the same (no error, no content) whether or not the account exists, whether
/// or not the request cap was hit and whether or not the provider accepted the e-mail. The only distinct answer is the
/// 503 for "e-mail not configured", which says nothing about any account. The e-mail itself is handed to a background
/// queue, so the response does not wait for the provider. Response time is NOT guaranteed identical: a known account
/// also does a database write that an unknown one skips (the unknown path only does the code hashing), so the
/// difference is small but not eliminated.
/// </summary>
public sealed class RequestPasswordResetCommandHandler
{
    private readonly IAuthRepository _authRepository;
    private readonly EmailCodeFlow _flow;
    private readonly IVerificationCodeService _codes;

    public RequestPasswordResetCommandHandler(IAuthRepository authRepository, EmailCodeFlow flow, IVerificationCodeService codes)
    {
        _authRepository = authRepository;
        _flow = flow;
        _codes = codes;
    }

    public async Task HandleAsync(RequestPasswordResetCommand command, CancellationToken cancellationToken)
    {
        _flow.EnsureEmailConfigured();

        var email = EmailAddress.From(command.Email).Value;
        var user = await _authRepository.FindUserByEmailAsync(email, cancellationToken);

        if (user is null || !user.IsActive)
        {
            _codes.Hash(Guid.Empty, EmailCodePurpose.PasswordReset, _codes.Generate());
            return;
        }

        await _flow.TryIssueAsync(user, EmailCodePurpose.PasswordReset, cancellationToken);
    }
}

/// <summary>
/// Sets a new password with the e-mailed code. The code is single use and has at most 5 attempts. On success every
/// refresh token of the user is deleted (all devices must sign in again) and the password rule of
/// <see cref="PasswordPolicy"/> applies.
/// </summary>
public sealed class ResetPasswordCommandHandler
{
    private readonly IAuthRepository _authRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly EmailCodeFlow _flow;

    public ResetPasswordCommandHandler(IAuthRepository authRepository, IPasswordHasher passwordHasher, EmailCodeFlow flow)
    {
        _authRepository = authRepository;
        _passwordHasher = passwordHasher;
        _flow = flow;
    }

    public async Task HandleAsync(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        _flow.EnsureEmailConfigured();

        var email = EmailAddress.From(command.Email).Value;

        // Before the code is touched: a weak password must not burn one of the code attempts.
        var problems = PasswordPolicy.Validate(command.NewPassword, email);
        if (problems.Count > 0)
        {
            throw new BadRequestException(
                "VALIDATION_ERROR",
                problems[0],
                new Dictionary<string, string[]> { ["NewPassword"] = problems.ToArray() });
        }

        var user = await _authRepository.FindUserByEmailAsync(email, cancellationToken);
        if (user is not null && !user.IsActive)
        {
            user = null;
        }

        var verified = await _flow.VerifyAsync(user, EmailCodePurpose.PasswordReset, command.Code, cancellationToken);

        // All or nothing: the code is consumed, the password saved and every session revoked together. If anything
        // fails the transaction rolls back, so the password never changes while an old refresh token survives.
        var newHash = _passwordHasher.HashPassword(command.NewPassword);
        await _authRepository.ExecuteInTransactionAsync(async () =>
        {
            await _flow.ConsumeAsync(verified, cancellationToken);
            user!.ChangePasswordHash(newHash);
            await _authRepository.SaveChangesAsync(cancellationToken);
            await _authRepository.RevokeRefreshTokensByUserIdAsync(user.Id, cancellationToken);
        }, cancellationToken);
    }
}
