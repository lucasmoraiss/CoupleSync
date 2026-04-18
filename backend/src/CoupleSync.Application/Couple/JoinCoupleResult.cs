namespace CoupleSync.Application.Couples;

public sealed record JoinCoupleResult(Guid CoupleId, IReadOnlyCollection<CoupleMemberDto> Members);
