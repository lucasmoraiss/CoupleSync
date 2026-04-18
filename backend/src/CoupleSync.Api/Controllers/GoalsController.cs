using System.Security.Claims;
using CoupleSync.Api.Contracts.Goals;
using CoupleSync.Api.Filters;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Goals.Commands;
using CoupleSync.Application.Goals.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/goals")]
public sealed class GoalsController : ControllerBase
{
    private readonly CreateGoalCommandHandler _createHandler;
    private readonly UpdateGoalCommandHandler _updateHandler;
    private readonly ArchiveGoalCommandHandler _archiveHandler;
    private readonly GetGoalsQueryHandler _getGoalsHandler;
    private readonly GetGoalByIdQueryHandler _getGoalByIdHandler;
    private readonly GetGoalProgressQueryHandler _getGoalProgressHandler;

    public GoalsController(
        CreateGoalCommandHandler createHandler,
        UpdateGoalCommandHandler updateHandler,
        ArchiveGoalCommandHandler archiveHandler,
        GetGoalsQueryHandler getGoalsHandler,
        GetGoalByIdQueryHandler getGoalByIdHandler,
        GetGoalProgressQueryHandler getGoalProgressHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _archiveHandler = archiveHandler;
        _getGoalsHandler = getGoalsHandler;
        _getGoalByIdHandler = getGoalByIdHandler;
        _getGoalProgressHandler = getGoalProgressHandler;
    }

    [HttpPost]
    [ProducesResponseType(typeof(GoalResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GoalResponse>> CreateGoal(
        [FromBody] CreateGoalRequest request,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var userId = GetAuthenticatedUserId();

        var deadline = request.Deadline.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(request.Deadline, DateTimeKind.Utc)
            : request.Deadline;

        var result = await _createHandler.HandleAsync(
            new CreateGoalCommand(
                coupleId,
                userId,
                request.Title,
                request.Description,
                request.TargetAmount,
                request.Currency ?? "BRL",
                deadline),
            cancellationToken);

        return CreatedAtAction(
            nameof(GetGoalById),
            new { id = result.Id },
            MapToResponse(result));
    }

    [HttpGet]
    [ProducesResponseType(typeof(GetGoalsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GetGoalsResponse>> GetGoals(
        [FromQuery] bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        var coupleId = GetAuthenticatedCoupleId();

        var result = await _getGoalsHandler.HandleAsync(
            new GetGoalsQuery(coupleId, includeArchived),
            cancellationToken);

        var response = new GetGoalsResponse(
            result.TotalCount,
            result.Items.Select(MapToResponse).ToList());

        return Ok(response);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(GoalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GoalResponse>> GetGoalById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();

        var result = await _getGoalByIdHandler.HandleAsync(
            new GetGoalByIdQuery(id, coupleId),
            cancellationToken);

        return Ok(MapToResponse(result));
    }

    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(GoalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GoalResponse>> UpdateGoal(
        Guid id,
        [FromBody] UpdateGoalRequest request,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();

        DateTime? deadline = null;
        if (request.Deadline.HasValue)
        {
            deadline = request.Deadline.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(request.Deadline.Value, DateTimeKind.Utc)
                : request.Deadline.Value;
        }

        var result = await _updateHandler.HandleAsync(
            new UpdateGoalCommand(id, coupleId, request.Title, request.Description, request.TargetAmount, deadline),
            cancellationToken);

        return Ok(MapToResponse(result));
    }

    [HttpDelete("{id:guid}/archive")]
    [ProducesResponseType(typeof(GoalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GoalResponse>> ArchiveGoal(
        Guid id,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();

        var result = await _archiveHandler.HandleAsync(
            new ArchiveGoalCommand(id, coupleId),
            cancellationToken);

        return Ok(MapToResponse(result));
    }

    [HttpGet("{id:guid}/progress")]
    [ProducesResponseType(typeof(GoalProgressResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GoalProgressResponse>> GetGoalProgress(
        Guid id,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();

        var result = await _getGoalProgressHandler.HandleAsync(
            new GetGoalProgressQuery(id, coupleId),
            cancellationToken);

        return Ok(new GoalProgressResponse(
            result.GoalId,
            result.Title,
            result.TargetAmount,
            result.ContributedAmount,
            result.ProgressPercent,
            result.IsAchieved,
            result.DaysRemaining,
            result.Status.ToString()));
    }

    private static GoalResponse MapToResponse(Application.Goals.Queries.GoalDto g)
        => new(g.Id, g.CreatedByUserId, g.Title, g.Description,
               g.TargetAmount, g.Currency, g.Deadline, g.Status.ToString(),
               g.CreatedAtUtc, g.UpdatedAtUtc);

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
