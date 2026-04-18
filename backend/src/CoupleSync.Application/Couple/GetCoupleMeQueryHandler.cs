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
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired session.");
        }

        if (!user.CoupleId.HasValue)
        {
            throw new NotFoundException("COUPLE_NOT_FOUND", "Couple was not found.");
        }

        var couple = await _coupleRepository.FindByIdWithMembersAsync(user.CoupleId.Value, cancellationToken);

        if (couple is null)
        {
            throw new NotFoundException("COUPLE_NOT_FOUND", "Couple was not found.");
        }

        var members = couple.Members
            .Select(member => new CoupleMemberDto(member.Id, member.Name, member.Email))
            .ToArray();

        return new GetCoupleMeResult(couple.Id, couple.JoinCode, couple.CreatedAtUtc, members);
    }
}
