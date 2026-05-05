using System.Security.Claims;
using CoupleSync.Api.Contracts.Income;
using CoupleSync.Api.Filters;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Income;
using CoupleSync.Application.Income.Commands;
using CoupleSync.Application.Income.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/incomes")]
public sealed class IncomesController : ControllerBase
{
    private readonly IncomeService _incomeService;

    public IncomesController(IncomeService incomeService)
    {
        _incomeService = incomeService;
    }

    /// <summary>Create a new income source for the authenticated user.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(IncomeSourceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IncomeSourceResponse>> CreateIncomeSource(
        [FromBody] CreateIncomeSourceRequest request,
        CancellationToken ct)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var userId = GetAuthenticatedUserId();

        var input = new CreateIncomeSourceInput(request.Name, request.Amount, request.Currency, request.IsShared);
        var dto = await _incomeService.CreateAsync(coupleId, userId, request.Month, input, ct);

        return StatusCode(StatusCodes.Status201Created, MapSourceResponse(dto));
    }

    /// <summary>Get all income sources for the current month (personal + partner + shared).</summary>
    [HttpGet("current")]
    [ProducesResponseType(typeof(MonthlyIncomeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MonthlyIncomeResponse>> GetCurrentMonthIncome(CancellationToken ct)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var userId = GetAuthenticatedUserId();

        var dto = await _incomeService.GetCurrentMonthIncomeAsync(coupleId, userId, ct);
        return Ok(MapMonthlyResponse(dto));
    }

    /// <summary>Get all income sources for a specific month (YYYY-MM).</summary>
    [HttpGet("{month}")]
    [ProducesResponseType(typeof(MonthlyIncomeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MonthlyIncomeResponse>> GetMonthIncome(string month, CancellationToken ct)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var userId = GetAuthenticatedUserId();

        var dto = await _incomeService.GetMonthlyIncomeAsync(coupleId, userId, month, ct);
        return Ok(MapMonthlyResponse(dto));
    }

    /// <summary>Update an income source (owner or shared only).</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(IncomeSourceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IncomeSourceResponse>> UpdateIncomeSource(
        Guid id,
        [FromBody] UpdateIncomeSourceRequest request,
        CancellationToken ct)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var userId = GetAuthenticatedUserId();

        var input = new UpdateIncomeSourceInput(request.Name, request.Amount, request.IsShared);
        var dto = await _incomeService.UpdateAsync(coupleId, userId, id, input, ct);

        return Ok(MapSourceResponse(dto));
    }

    /// <summary>Delete an income source (owner or shared only).</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteIncomeSource(Guid id, CancellationToken ct)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var userId = GetAuthenticatedUserId();

        await _incomeService.DeleteAsync(coupleId, userId, id, ct);
        return NoContent();
    }

    private static IncomeSourceResponse MapSourceResponse(IncomeSourceDto dto)
        => new(dto.Id, dto.UserId, dto.Name, dto.Amount, dto.Currency, dto.IsShared, dto.CreatedAtUtc, dto.UpdatedAtUtc);

    private static MonthlyIncomeResponse MapMonthlyResponse(MonthlyIncomeDto dto)
    {
        var personal = MapGroupResponse(dto.PersonalIncome);
        var partner = dto.PartnerIncome is not null ? MapGroupResponse(dto.PartnerIncome) : null;
        var shared = MapGroupResponse(dto.SharedIncome);

        return new MonthlyIncomeResponse(dto.Month, dto.Currency, personal, partner, shared, dto.CoupleTotal);
    }

    private static IncomeGroupResponse MapGroupResponse(IncomeGroupDto group)
        => new(group.UserId, group.UserName, group.Sources.Select(MapSourceResponse).ToList(), group.Total);

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired couple context.");
        return coupleId;
    }

    private Guid GetAuthenticatedUserId()
    {
        var claimValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claimValue, out var userId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired user context.");
        return userId;
    }
}
