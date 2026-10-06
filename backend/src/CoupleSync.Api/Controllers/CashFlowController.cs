using System.Security.Claims;
using CoupleSync.Api.Contracts.CashFlow;
using CoupleSync.Api.Filters;
using CoupleSync.Application.CashFlow.Queries;
using CoupleSync.Application.Common.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/cashflow")]
public sealed class CashFlowController : ControllerBase
{
    private readonly GetCashFlowQueryHandler _handler;

    public CashFlowController(GetCashFlowQueryHandler handler)
    {
        _handler = handler;
    }

    [HttpGet]
    [ProducesResponseType(typeof(GetCashFlowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GetCashFlowResponse>> GetCashFlow(
        [FromQuery] int horizon,
        CancellationToken cancellationToken)
    {
        if (horizon != 30 && horizon != 90)
            throw new BadRequestException("INVALID_HORIZON", "O horizonte deve ser 30 ou 90 dias.");

        var coupleId = GetAuthenticatedCoupleId();

        var result = await _handler.HandleAsync(
            new GetCashFlowQuery(coupleId, horizon),
            cancellationToken);

        return Ok(new GetCashFlowResponse(
            result.Horizon,
            result.HistoricalPeriodStart,
            result.HistoricalPeriodEnd,
            result.TransactionCount,
            result.TotalHistoricalSpend,
            result.AverageDailySpend,
            result.ProjectedSpend,
            result.CategoryBreakdown,
            result.Assumptions,
            result.GeneratedAtUtc,
            result.Month,
            result.MonthIncome,
            result.MonthSpentToDate,
            result.ForecastDailyAverage,
            result.RemainingDays,
            result.ForecastRemainingSpend,
            result.ProjectedMonthEndBalance));
    }

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        return coupleId;
    }
}
