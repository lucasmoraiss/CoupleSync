using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Couples;

/// <param name="CoupleId">The group of the requester's token (null when the token has none).</param>
public sealed record RemoveCoupleMemberCommand(Guid RequesterUserId, Guid MemberUserId, Guid? CoupleId);

/// <summary>
/// The owner removes another member from the group of the owner's token. The group stops sending
/// the removed member alerts and their access token is refused there by the membership check as soon as this
/// commits. When it was the member's active group, they are left with NO active group (never moved to another
/// one by someone else's action) and their refresh token is revoked, so they sign in again and choose.
/// Their Open Finance in the group (connection, credentials, items and accounts) is deleted in the same save.
/// </summary>
public sealed class RemoveCoupleMemberCommandHandler
{
    private readonly ICoupleRepository _coupleRepository;
    private readonly ICoupleMembership _membership;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly IPluggyClient _pluggy;

    public RemoveCoupleMemberCommandHandler(
        ICoupleRepository coupleRepository,
        ICoupleMembership membership,
        IDateTimeProvider dateTimeProvider,
        IPluggyClient pluggy)
    {
        _coupleRepository = coupleRepository;
        _membership = membership;
        _dateTimeProvider = dateTimeProvider;
        _pluggy = pluggy;
    }

    public async Task HandleAsync(RemoveCoupleMemberCommand command, CancellationToken cancellationToken)
    {
        var coupleId = command.CoupleId
            ?? throw new NotFoundException("COUPLE_NOT_FOUND", "Você não está em nenhum grupo no momento.");

        // Authorised before anything is locked: only the group's owner may make the server lock another
        // user's row. Checked again below, after the locks, because ownership may change while waiting.
        if (!await _membership.IsOwnerAsync(command.RequesterUserId, coupleId, cancellationToken))
        {
            throw new ForbiddenException("NOT_COUPLE_OWNER", "Só quem criou o grupo pode remover membros.");
        }

        // Locks the member being removed (their memberships and active group change), then the group.
        await using var change = await _coupleRepository.BeginMembershipChangeAsync(command.MemberUserId, cancellationToken);
        var requester = await _coupleRepository.FindUserByIdAsync(command.RequesterUserId, cancellationToken);

        if (requester is null || !requester.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        await change.LockCoupleAsync(coupleId, cancellationToken);

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

        var member = couple.Members.SingleOrDefault(x => x.UserId == command.MemberUserId)?.User
            ?? throw new NotFoundException("MEMBER_NOT_FOUND", "Esse membro não faz parte do grupo.");

        couple.RemoveMember(member, _dateTimeProvider.UtcNow);

        // Their devices follow them: they keep receiving the alerts of the groups they are still in.
        var remaining = (await _coupleRepository.GetGroupsOfUserAsync(member.Id, cancellationToken))
            .Where(g => g.CoupleId != coupleId)
            .ToList();
        var remainingCoupleId = remaining.Any(g => g.CoupleId == member.ActiveCoupleId)
            ? member.ActiveCoupleId
            : remaining.FirstOrDefault()?.CoupleId;
        await _coupleRepository.StopDeliveriesToMemberAsync(member.Id, coupleId, remainingCoupleId, cancellationToken);

        // Someone working in another group (or in none) is not disturbed: same session, same active group.
        if (member.ActiveCoupleId == coupleId)
        {
            member.ClearActiveCouple();
            await _coupleRepository.RevokeRefreshTokenAsync(member.Id, cancellationToken);
        }

        var bankConnectionIds = await _coupleRepository.RemoveOpenFinanceOfMemberAsync(member.Id, coupleId, cancellationToken);

        await _coupleRepository.SaveChangesAsync(cancellationToken);
        await change.CommitAsync(cancellationToken);

        // Only once the connections are really gone: the Pluggy API keys kept for them are dropped.
        foreach (var connectionId in bankConnectionIds)
        {
            _pluggy.ForgetConnection(connectionId);
        }
    }
}
