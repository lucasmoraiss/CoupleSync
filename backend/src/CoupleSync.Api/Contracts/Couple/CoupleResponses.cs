namespace CoupleSync.Api.Contracts.Couple;

public sealed record CoupleMemberResponse(Guid UserId, string Name, string Email);

public sealed record JoinCoupleResponse(Guid CoupleId, IReadOnlyCollection<CoupleMemberResponse> Members, string AccessToken, string? RefreshToken);

public sealed record GetCoupleMeResponse(
    Guid CoupleId,
    string JoinCode,
    DateTime CreatedAtUtc,
    IReadOnlyCollection<CoupleMemberResponse> Members,
    Guid? OwnerUserId,
    DateTime JoinCodeExpiresAtUtc);

/// <param name="ActiveCoupleId">The group that is active after leaving: another of the user's groups, or null.</param>
public sealed record LeaveCoupleResponse(string AccessToken, string RefreshToken, Guid? ActiveCoupleId);

public sealed record SwitchCoupleRequest(Guid CoupleId);

public sealed record SwitchCoupleResponse(Guid CoupleId, string AccessToken, string RefreshToken);

public sealed record MyGroupMemberResponse(Guid UserId, string Name);

public sealed record MyGroupResponse(
    Guid CoupleId,
    string Name,
    bool IsOwner,
    bool IsActive,
    DateTime JoinedAtUtc,
    IReadOnlyCollection<MyGroupMemberResponse> Members);

public sealed record MyGroupsResponse(Guid? ActiveCoupleId, int MaxGroups, IReadOnlyCollection<MyGroupResponse> Groups);

public sealed record RegenerateJoinCodeResponse(string JoinCode, DateTime JoinCodeExpiresAtUtc);