namespace CoupleSync.Api.Contracts.Couple;

public sealed record CoupleMemberResponse(Guid UserId, string Name, string Email);

public sealed record JoinCoupleResponse(Guid CoupleId, IReadOnlyCollection<CoupleMemberResponse> Members);

public sealed record GetCoupleMeResponse(Guid CoupleId, string JoinCode, DateTime CreatedAtUtc, IReadOnlyCollection<CoupleMemberResponse> Members);