using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Couples;

/// <param name="CurrentCoupleId">The group of the caller's token (null when the token has none).</param>
public sealed record GetMyGroupsQuery(Guid UserId, Guid? CurrentCoupleId);

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

        // "Active" is the group this caller is working in, i.e. the one in their token (the one every data route
        // answers for), and only while they still belong to it.
        var activeCoupleId = groups.Any(g => g.CoupleId == query.CurrentCoupleId) ? query.CurrentCoupleId : null;

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
