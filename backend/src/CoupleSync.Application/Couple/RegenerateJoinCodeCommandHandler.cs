using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Couples;

/// <param name="CoupleId">The group of the caller's token (null when the token has none).</param>
public sealed record RegenerateJoinCodeCommand(Guid UserId, Guid? CoupleId);

public sealed record RegenerateJoinCodeResult(string JoinCode, DateTime JoinCodeExpiresAtUtc);

/// <summary>The group owner replaces the invite code: the previous one stops working at once, the new one lasts 7 days.</summary>
public sealed class RegenerateJoinCodeCommandHandler
{
    private const int MaxJoinCodeAttempts = 20;

    private readonly ICoupleRepository _coupleRepository;
    private readonly ICoupleMembership _membership;
    private readonly ICoupleJoinCodeGenerator _joinCodeGenerator;
    private readonly IDateTimeProvider _dateTimeProvider;

    public RegenerateJoinCodeCommandHandler(
        ICoupleRepository coupleRepository,
        ICoupleMembership membership,
        ICoupleJoinCodeGenerator joinCodeGenerator,
        IDateTimeProvider dateTimeProvider)
    {
        _coupleRepository = coupleRepository;
        _membership = membership;
        _joinCodeGenerator = joinCodeGenerator;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<RegenerateJoinCodeResult> HandleAsync(RegenerateJoinCodeCommand command, CancellationToken cancellationToken)
    {
        await using var change = await _coupleRepository.BeginMembershipChangeAsync(command.UserId, cancellationToken);
        var user = await _coupleRepository.FindUserByIdAsync(command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        var coupleId = command.CoupleId
            ?? throw new NotFoundException("COUPLE_NOT_FOUND", "Você não está em nenhum grupo no momento.");

        // Ownership is read after the lock: an owner whose exit is being committed no longer renews the code
        // (otherwise an emptied group could end with a valid code).
        await change.LockCoupleAsync(coupleId, cancellationToken);

        if (!await _membership.IsOwnerAsync(user.Id, coupleId, cancellationToken))
        {
            throw new ForbiddenException("NOT_COUPLE_OWNER", "Só quem criou o grupo pode gerar um novo código.");
        }

        var couple = await _coupleRepository.FindByIdWithMembersAsync(coupleId, cancellationToken)
            ?? throw new NotFoundException("COUPLE_NOT_FOUND", "Casal não encontrado.");

        var joinCode = await GenerateUniqueJoinCodeAsync(cancellationToken);
        couple.RegenerateJoinCode(joinCode, _dateTimeProvider.UtcNow);
        await _coupleRepository.SaveChangesAsync(cancellationToken);
        await change.CommitAsync(cancellationToken);

        return new RegenerateJoinCodeResult(couple.JoinCode, couple.JoinCodeExpiresAtUtc);
    }

    private async Task<string> GenerateUniqueJoinCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxJoinCodeAttempts; attempt++)
        {
            var joinCode = _joinCodeGenerator.Generate();
            if (!await _coupleRepository.JoinCodeExistsAsync(joinCode, cancellationToken))
            {
                return joinCode;
            }
        }

        throw new AppException("COUPLE_CODE_GENERATION_FAILED", "Não foi possível gerar o código de convite. Tente novamente.", 500);
    }
}
