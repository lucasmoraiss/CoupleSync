namespace CoupleSync.Application.Couples;

public sealed record GetCoupleMeResult(
    Guid CoupleId,
    string JoinCode,
    DateTime CreatedAtUtc,
    IReadOnlyCollection<CoupleMemberDto> Members);
