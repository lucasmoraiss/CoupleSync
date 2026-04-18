namespace CoupleSync.Application.Goals.Commands;

public sealed record LinkTransactionToGoalCommand(
    Guid TransactionId,
    Guid? GoalId,
    Guid CoupleId);
