using System.Security.Claims;
using CoupleSync.Api.Contracts.Budget;
using CoupleSync.Api.Filters;
using CoupleSync.Application.Budget;
using CoupleSync.Application.Budget.Commands;
using CoupleSync.Application.Budget.Queries;
using CoupleSync.Application.Common.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/budgets")]
public sealed class BudgetController : ControllerBase
{
    private readonly BudgetService _budgetService;

    public BudgetController(BudgetService budgetService)
    {
        _budgetService = budgetService;
    }

    /// <summary>Create or upsert a monthly budget plan for the authenticated couple.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(BudgetPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BudgetPlanResponse>> UpsertBudgetPlan(
        [FromBody] CreateBudgetPlanRequest request,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();

        var dto = await _budgetService.UpsertPlanAsync(
            coupleId,
            request.Month,
            request.GrossIncome,
            request.Currency,
            cancellationToken);

        return Ok(MapToResponse(dto));
    }

    /// <summary>Get the current calendar-month budget plan for the authenticated couple.</summary>
    [HttpGet("current")]
    [ProducesResponseType(typeof(BudgetPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BudgetPlanResponse>> GetCurrentBudgetPlan(
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();

        var dto = await _budgetService.GetCurrentPlanAsync(coupleId, cancellationToken);

        if (dto is null)
            return NotFound(new { code = "BUDGET_PLAN_NOT_FOUND", message = "No budget plan for the current month." });

        return Ok(MapToResponse(dto));
    }

    /// <summary>Get the budget plan for a specific month (YYYY-MM).</summary>
    [HttpGet("{month}")]
    [ProducesResponseType(typeof(BudgetPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BudgetPlanResponse>> GetBudgetPlanByMonth(
        string month,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();

        var dto = await _budgetService.GetPlanAsync(coupleId, month, cancellationToken);

        if (dto is null)
            return NotFound(new { code = "BUDGET_PLAN_NOT_FOUND", message = $"No budget plan for month '{month}'." });

        return Ok(MapToResponse(dto));
    }

    /// <summary>Replace all allocations for a budget plan (transactional, max 20).</summary>
    [HttpPut("{planId:guid}/allocations")]
    [ProducesResponseType(typeof(BudgetPlanResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BudgetPlanResponse>> ReplaceAllocations(
        Guid planId,
        [FromBody] ReplaceAllocationsRequest request,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();

        var inputs = request.Allocations
            .Select(a => new AllocationInput(a.Category, a.AllocatedAmount, a.Currency))
            .ToList();

        var dto = await _budgetService.ReplaceAllocationsAsync(
            coupleId,
            planId,
            inputs,
            cancellationToken);

        return Ok(MapToResponse(dto));
    }

    /// <summary>Update gross income for the current month (auto-creates plan if none exists).</summary>
    [HttpPatch("income")]
    [ProducesResponseType(typeof(UpdateIncomeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<UpdateIncomeResponse>> UpdateIncome(
        [FromBody] UpdateIncomeRequest request,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var currency = request.Currency ?? "BRL";

        var dto = await _budgetService.UpdateIncomeAsync(
            coupleId,
            request.GrossIncome,
            currency,
            cancellationToken);

        return Ok(new UpdateIncomeResponse(dto.Id, dto.Month, dto.GrossIncome, dto.Currency));
    }

    private static BudgetPlanResponse MapToResponse(BudgetPlanDto dto)
    {
        var allocations = dto.Allocations
            .Select(a => new BudgetAllocationResponse(a.Id, a.Category, a.AllocatedAmount, a.Currency, a.ActualSpent, a.Remaining))
            .ToList();

        return new BudgetPlanResponse(
            dto.Id,
            dto.Month,
            dto.GrossIncome,
            dto.Currency,
            allocations,
            dto.BudgetGap,
            dto.CreatedAtUtc,
            dto.UpdatedAtUtc);
    }

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired couple context.");
        return coupleId;
    }
}
