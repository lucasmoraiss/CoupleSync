using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Couples;

public sealed class GetCoupleMeQueryHandler
{
    private readonly ICoupleRepository _coupleRepository;

    public GetCoupleMeQueryHandler(ICoupleRepository coupleRepository)
    {
        _coupleRepository = coupleRepository;
    }

    public async Task<GetCoupleMeResult> HandleAsync(GetCoupleMeQuery query, CancellationToken cancellationToken)
    {
        var user = await _coupleRepository.FindUserByIdAsync(query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        if (!user.ActiveCoupleId.HasValue)
        {
            throw new NotFoundException("COUPLE_NOT_FOUND", "Casal não encontrado.");
        }

        var couple = await _coupleRepository.FindByIdWithMembersAsync(user.ActiveCoupleId.Value, cancellationToken);

        // The active group is only a pointer: the group is shown to its members and to nobody else.
        if (couple is null || !couple.HasMember(user.Id))
        {
            throw new NotFoundException("COUPLE_NOT_FOUND", "Casal não encontrado.");
        }

        var members = couple.Members
            .OrderBy(member => member.JoinedAtUtc)
            .ThenBy(member => member.UserId)
            .Select(member => new CoupleMemberDto(member.UserId, member.User.Name, member.User.Email))
            .ToArray();

        return new GetCoupleMeResult(couple.Id, couple.JoinCode, couple.CreatedAtUtc, members, couple.OwnerUserId, couple.JoinCodeExpiresAtUtc);
    }
}
