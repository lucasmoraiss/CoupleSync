using System.Security.Claims;
using CoupleSync.Api.Contracts.Reports;
using CoupleSync.Api.Filters;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly ReportsService _service;

    public ReportsController(ReportsService service)
    {
        _service = service;
    }

    /// <summary>Returns total spending grouped by category for the last N complete months.</summary>
    [HttpGet("spending-by-category")]
    [ProducesResponseType(typeof(SpendingByCategoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SpendingByCategoryResponse>> SpendingByCategory(
        [FromQuery] int months = 6,
        CancellationToken ct = default)
    {
        if (months is < 1 or > 60)
            return BadRequest("months must be between 1 and 60.");

        var coupleId = GetAuthenticatedCoupleId();
        var result = await _service.GetSpendingByCategoryAsync(coupleId, months, ct);

        var response = new SpendingByCategoryResponse(
            result.Categories
                .Select(c => new CategorySpendingItemResponse(c.Name, c.Total, c.Percentage, c.Color))
                .ToList());

        return Ok(response);
    }

    /// <summary>Returns monthly spending trends for the last N months.</summary>
    [HttpGet("monthly-trends")]
    [ProducesResponseType(typeof(MonthlyTrendsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MonthlyTrendsResponse>> MonthlyTrends(
        [FromQuery] int months = 12,
        CancellationToken ct = default)
    {
        if (months is < 1 or > 60)
            return BadRequest("months must be between 1 and 60.");

        var coupleId = GetAuthenticatedCoupleId();
        var result = await _service.GetMonthlyTrendsAsync(coupleId, months, ct);

        var response = new MonthlyTrendsResponse(
            result.Months
                .Select(m => new MonthlyTrendItemResponse(m.Month, m.Income, m.Expense, m.Net))
                .ToList());

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
