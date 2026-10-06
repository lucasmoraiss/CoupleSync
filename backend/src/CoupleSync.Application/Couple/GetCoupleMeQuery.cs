namespace CoupleSync.Application.Couples;

/// <param name="CoupleId">The group of the caller's token (null when the token has none).</param>
public sealed record GetCoupleMeQuery(Guid UserId, Guid? CoupleId);
