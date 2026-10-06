namespace CoupleSync.Api.Contracts.Couple;

public sealed record CoupleMemberResponse(Guid UserId, string Name, string Email);

public sealed record JoinCoupleResponse(Guid CoupleId, IReadOnlyCollection<CoupleMemberResponse> Members, string AccessToken);

public sealed record GetCoupleMeResponse(
    Guid CoupleId,
    string JoinCode,
    DateTime CreatedAtUtc,
    IReadOnlyCollection<CoupleMemberResponse> Members,
    Guid? OwnerUserId,
    DateTime JoinCodeExpiresAtUtc);

public sealed record LeaveCoupleResponse(string AccessToken, string RefreshToken);

public sealed record RegenerateJoinCodeResponse(string JoinCode, DateTime JoinCodeExpiresAtUtc);