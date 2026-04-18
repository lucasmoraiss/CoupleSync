namespace CoupleSync.Application.CashFlow.Queries;

public sealed record GetCashFlowQuery(Guid CoupleId, int Horizon);
