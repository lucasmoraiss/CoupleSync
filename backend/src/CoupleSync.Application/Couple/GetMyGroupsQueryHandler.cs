using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Couples;

public sealed record GetMyGroupsQuery(Guid UserId);

public sealed record MyGroupDto(
    Guid CoupleId,
    string Name,
    bool IsOwner,
    bool IsActive,
    DateTime JoinedAtUtc,
    IReadOnlyList<GroupMemberName> Members);

public sealed record GetMyGroupsResult(Guid? ActiveCoupleId, int MaxGroups, IReadOnlyList<MyGroupDto> Groups);

/// <summary>The groups the caller belongs to, and only those: nothing here is looked up by a group id from the request.</summary>
public sealed class GetMyGroupsQueryHandler
{
    private readonly ICoupleRepository _coupleRepository;

    public GetMyGroupsQueryHandler(ICoupleRepository coupleRepository)
    {
        _coupleRepository = coupleRepository;
    }

    public async Task<GetMyGroupsResult> HandleAsync(GetMyGroupsQuery query, CancellationToken cancellationToken)
    {
        var user = await _coupleRepository.FindUserByIdAsync(query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        var groups = await _coupleRepository.GetGroupsOfUserAsync(user.Id, cancellationToken);

        // An active pointer to a group the user is no longer in is not reported as active.
        var activeCoupleId = groups.Any(g => g.CoupleId == user.ActiveCoupleId) ? user.ActiveCoupleId : null;

        var items = groups
            .Select(g => new MyGroupDto(
                g.CoupleId,
                g.LabelFor(user.Id),
                g.Role == CoupleRole.Owner,
                g.CoupleId == activeCoupleId,
                g.JoinedAtUtc,
                g.Members))
            .ToList();

        return new GetMyGroupsResult(activeCoupleId, User.MaxGroups, items);
    }
}
