using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Couples;

public sealed record RemoveCoupleMemberCommand(Guid RequesterUserId, Guid MemberUserId);

/// <summary>
/// The group owner removes another member. The removed member's refresh token is revoked (their next
/// renewal fails and they sign in again, without a group) and the group stops sending them alerts.
/// Their access token is refused by the membership check as soon as this commits.
/// </summary>
public sealed class RemoveCoupleMemberCommandHandler
{
    private readonly ICoupleRepository _coupleRepository;
    private readonly ICoupleMembership _membership;
    private readonly IDateTimeProvider _dateTimeProvider;

    public RemoveCoupleMemberCommandHandler(
        ICoupleRepository coupleRepository,
        ICoupleMembership membership,
        IDateTimeProvider dateTimeProvider)
    {
        _coupleRepository = coupleRepository;
        _membership = membership;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task HandleAsync(RemoveCoupleMemberCommand command, CancellationToken cancellationToken)
    {
        var requester = await _coupleRepository.FindUserByIdAsync(command.RequesterUserId, cancellationToken);

        if (requester is null || !requester.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        if (!requester.CoupleId.HasValue)
        {
            throw new NotFoundException("COUPLE_NOT_FOUND", "Você não faz parte de nenhum grupo.");
        }

        var coupleId = requester.CoupleId.Value;

        if (!await _membership.IsOwnerAsync(requester.Id, coupleId, cancellationToken))
        {
            throw new ForbiddenException("NOT_COUPLE_OWNER", "Só quem criou o grupo pode remover membros.");
        }

        if (command.MemberUserId == requester.Id)
        {
            throw new BadRequestException("CANNOT_REMOVE_SELF", "Para sair do grupo, use a opção Sair do grupo.");
        }

        var couple = await _coupleRepository.FindByIdWithMembersAsync(coupleId, cancellationToken)
            ?? throw new NotFoundException("COUPLE_NOT_FOUND", "Casal não encontrado.");

        var member = couple.Members.SingleOrDefault(x => x.Id == command.MemberUserId)
            ?? throw new NotFoundException("MEMBER_NOT_FOUND", "Esse membro não faz parte do grupo.");

        couple.RemoveMember(member, _dateTimeProvider.UtcNow);
        await _coupleRepository.StopDeliveriesToMemberAsync(member.Id, coupleId, cancellationToken);
        await _coupleRepository.RevokeRefreshTokenAsync(member.Id, cancellationToken);
        await _coupleRepository.SaveChangesAsync(cancellationToken);
    }
}
