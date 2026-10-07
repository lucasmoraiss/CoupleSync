namespace CoupleSync.Application.Goals.Commands;

/// <summary>
/// ManualAmount sets the manually saved amount. CurrentAmount is the legacy field of installed apps, which
/// show (and send back) the unified progress: it is the desired total, so the manual amount becomes
/// total minus the linked transactions. ManualAmount wins when both are sent.
/// </summary>
public sealed record UpdateGoalCommand(
    Guid Id,
    Guid CoupleId,
    string? Title,
    string? Description,
    decimal? TargetAmount,
    decimal? CurrentAmount,
    DateTime? Deadline,
    decimal? ManualAmount = null);
