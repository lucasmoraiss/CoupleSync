using System.Security.Claims;
using CoupleSync.Api.Contracts.Dashboard;
using CoupleSync.Api.Filters;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/dashboard")]
public sealed class DashboardController : ControllerBase
{
    private readonly GetDashboardQueryHandler _handler;

    public DashboardController(GetDashboardQueryHandler handler)
    {
        _handler = handler;
    }

    [HttpGet]
    [ProducesResponseType(typeof(GetDashboardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GetDashboardResponse>> GetDashboard(
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken cancellationToken = default)
    {
        if (startDate.HasValue && startDate.Value.Kind == DateTimeKind.Unspecified)
            startDate = DateTime.SpecifyKind(startDate.Value, DateTimeKind.Utc);
        if (endDate.HasValue && endDate.Value.Kind == DateTimeKind.Unspecified)
            endDate = DateTime.SpecifyKind(endDate.Value, DateTimeKind.Utc);

        if (startDate.HasValue && endDate.HasValue && startDate > endDate)
            return BadRequest("startDate must not be after endDate");

        var coupleId = GetAuthenticatedCoupleId();

        var result = await _handler.HandleAsync(
            new GetDashboardQuery(coupleId, startDate, endDate),
            cancellationToken);

        var response = new GetDashboardResponse(
            result.TotalExpenses,
            result.ExpensesByCategory.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            result.PartnerBreakdown.Select(p => new PartnerBreakdownResponse(p.UserId, p.TotalAmount)).ToList(),
            result.TransactionCount,
            result.PeriodStart,
            result.PeriodEnd,
            result.GeneratedAtUtc);

        return Ok(response);
    }

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired couple context.");
        return coupleId;
    }
}
