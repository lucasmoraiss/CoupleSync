using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Auth;

public sealed record ConfirmEmailCommand(Guid UserId, string Code);

public sealed record ResendEmailVerificationCommand(Guid UserId);

public sealed record CurrentUserResult(Guid Id, string Email, string Name, bool EmailVerified);

/// <summary>Confirms the signed-in user e-mail with the code sent at sign-up (or on resend). Idempotent once confirmed.</summary>
public sealed class ConfirmEmailCommandHandler
{
    private readonly IAuthRepository _authRepository;
    private readonly EmailCodeFlow _flow;

    public ConfirmEmailCommandHandler(IAuthRepository authRepository, EmailCodeFlow flow)
    {
        _authRepository = authRepository;
        _flow = flow;
    }

    public async Task HandleAsync(ConfirmEmailCommand command, CancellationToken cancellationToken)
    {
        _flow.EnsureEmailConfigured();

        var user = await _authRepository.FindUserByIdAsync(command.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        if (user.EmailVerified)
        {
            return;
        }

        var verified = await _flow.VerifyAsync(user, EmailCodePurpose.EmailVerification, command.Code, cancellationToken);

        await _authRepository.ExecuteInTransactionAsync(async () =>
        {
            await _flow.ConsumeAsync(verified, cancellationToken);
            user.MarkEmailVerified();
            await _authRepository.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }
}

/// <summary>Sends a new confirmation code (the previous one stops working). The account is the caller own, so hitting the cap is reported.</summary>
public sealed class ResendEmailVerificationCommandHandler
{
    private readonly IAuthRepository _authRepository;
    private readonly EmailCodeFlow _flow;

    public ResendEmailVerificationCommandHandler(IAuthRepository authRepository, EmailCodeFlow flow)
    {
        _authRepository = authRepository;
        _flow = flow;
    }

    public async Task HandleAsync(ResendEmailVerificationCommand command, CancellationToken cancellationToken)
    {
        _flow.EnsureEmailConfigured();

        var user = await _authRepository.FindUserByIdAsync(command.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        if (user.EmailVerified)
        {
            return;
        }

        if (await _flow.IssueAsync(user, EmailCodePurpose.EmailVerification, cancellationToken) == CodeIssueOutcome.Throttled)
        {
            throw new AppException(
                "RATE_LIMIT_EXCEEDED",
                "Você já pediu muitos códigos. Aguarde um pouco e tente novamente.",
                429);
        }
    }
}

public sealed class GetCurrentUserQueryHandler
{
    private readonly IAuthRepository _authRepository;

    public GetCurrentUserQueryHandler(IAuthRepository authRepository)
    {
        _authRepository = authRepository;
    }

    public async Task<CurrentUserResult> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _authRepository.FindUserByIdAsync(userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        return new CurrentUserResult(user.Id, user.Email, user.Name, user.EmailVerified);
    }
}
