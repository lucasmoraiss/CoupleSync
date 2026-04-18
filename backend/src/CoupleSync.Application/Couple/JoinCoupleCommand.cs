namespace CoupleSync.Application.Couples;

public sealed record JoinCoupleCommand(Guid UserId, string JoinCode);
