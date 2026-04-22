using System.Security.Claims;
using CoupleSync.Api.Contracts.Transactions;
using CoupleSync.Api.Filters;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Goals.Commands;
using CoupleSync.Application.Transactions.Commands;
using CoupleSync.Application.Transactions.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/transactions")]
public sealed class TransactionsController : ControllerBase
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    private readonly GetTransactionsQueryHandler _getTransactionsHandler;
    private readonly UpdateTransactionCategoryCommandHandler _updateCategoryHandler;
    private readonly LinkTransactionToGoalCommandHandler _linkToGoalHandler;
    private readonly CreateManualTransactionCommandHandler _createManualHandler;

    public TransactionsController(
        GetTransactionsQueryHandler getTransactionsHandler,
        UpdateTransactionCategoryCommandHandler updateCategoryHandler,
        LinkTransactionToGoalCommandHandler linkToGoalHandler,
        CreateManualTransactionCommandHandler createManualHandler)
    {
        _getTransactionsHandler = getTransactionsHandler;
        _updateCategoryHandler = updateCategoryHandler;
        _linkToGoalHandler = linkToGoalHandler;
        _createManualHandler = createManualHandler;
    }

    [HttpGet]
    [ProducesResponseType(typeof(GetTransactionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GetTransactionsResponse>> GetTransactions(
        [FromQuery] int page = DefaultPage,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] string? category = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = DefaultPage;
        if (pageSize < 1) pageSize = DefaultPageSize;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        if (startDate.HasValue && startDate.Value.Kind == DateTimeKind.Unspecified)
            startDate = DateTime.SpecifyKind(startDate.Value, DateTimeKind.Utc);
        if (endDate.HasValue && endDate.Value.Kind == DateTimeKind.Unspecified)
            endDate = DateTime.SpecifyKind(endDate.Value, DateTimeKind.Utc);

        var coupleId = GetAuthenticatedCoupleId();

        var result = await _getTransactionsHandler.HandleAsync(
            new GetTransactionsQuery(coupleId, page, pageSize, category, startDate, endDate),
            cancellationToken);

        var response = new GetTransactionsResponse(
            result.TotalCount,
            result.Page,
            result.PageSize,
            result.Items.Select(t => new TransactionResponse(
                t.Id, t.UserId, t.Bank, t.Amount, t.Currency,
                t.EventTimestampUtc, t.Description, t.Merchant, t.Category, t.CreatedAtUtc))
            .ToList());

        return Ok(response);
    }

    [HttpPatch("{id:guid}/category")]
    [ProducesResponseType(typeof(TransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TransactionResponse>> PatchCategory(
        Guid id,
        [FromBody] PatchTransactionCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();

        var result = await _updateCategoryHandler.HandleAsync(
            new UpdateTransactionCategoryCommand(id, coupleId, request.Category),
            cancellationToken);

        return Ok(new TransactionResponse(
            result.Id, result.UserId, result.Bank, result.Amount, result.Currency,
            result.EventTimestampUtc, result.Description, result.Merchant, result.Category, result.CreatedAtUtc));
    }

    /// <summary>
    /// Creates a transaction manually entered by the user (no bank notification / no OCR).
    /// Useful when the user wants to record an expense on the fly.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TransactionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TransactionResponse>> CreateManual(
        [FromBody] CreateManualTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var userId = GetAuthenticatedUserId();

        var currency = string.IsNullOrWhiteSpace(request.Currency) ? "BRL" : request.Currency;
        var eventTs = request.EventTimestampUtc ?? DateTime.UtcNow;
        if (eventTs.Kind == DateTimeKind.Unspecified)
            eventTs = DateTime.SpecifyKind(eventTs, DateTimeKind.Utc);

        var transaction = await _createManualHandler.HandleAsync(
            new CreateManualTransactionCommand(
                coupleId, userId, request.Amount, currency, eventTs,
                request.Description, request.Merchant, request.Category),
            cancellationToken);

        var response = new TransactionResponse(
            transaction.Id, transaction.UserId, transaction.Bank, transaction.Amount, transaction.Currency,
            transaction.EventTimestampUtc, transaction.Description, transaction.Merchant, transaction.Category,
            transaction.CreatedAtUtc);

        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPatch("{id:guid}/goal")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchGoal(
        Guid id,
        [FromBody] PatchTransactionGoalRequest request,
        CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();

        await _linkToGoalHandler.HandleAsync(
            new LinkTransactionToGoalCommand(id, request.GoalId, coupleId),
            cancellationToken);

        return NoContent();
    }

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired couple context.");
        return coupleId;
    }

    private Guid GetAuthenticatedUserId()
    {
        var claimValue = User.FindFirstValue("user_id");
        if (!Guid.TryParse(claimValue, out var userId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired session.");
        return userId;
    }
}
