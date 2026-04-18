using System.Security.Claims;
using CoupleSync.Api.Contracts.Integrations;
using CoupleSync.Api.Filters;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.NotificationCapture;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/integrations")]
public sealed class IntegrationsController : ControllerBase
{
    private readonly IngestNotificationEventCommandHandler _ingestHandler;
    private readonly GetIntegrationStatusQueryHandler _statusQueryHandler;

    public IntegrationsController(
        IngestNotificationEventCommandHandler ingestHandler,
        GetIntegrationStatusQueryHandler statusQueryHandler)
    {
        _ingestHandler = ingestHandler;
        _statusQueryHandler = statusQueryHandler;
    }

    [HttpPost("events")]
    [ProducesResponseType(typeof(IngestNotificationEventResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IngestNotificationEventResponse>> IngestEvent(
        [FromBody] IngestNotificationEventRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();
        var coupleId = GetAuthenticatedCoupleId();

        var result = await _ingestHandler.HandleAsync(
            new IngestNotificationEventCommand(
                userId,
                coupleId,
                request.Bank,
                request.Amount,
                request.Currency,
                request.EventTimestamp,
                request.Description,
                request.Merchant,
                request.RawNotificationText),
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created,
            new IngestNotificationEventResponse(result.IngestId, result.Status));
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(IntegrationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IntegrationStatusResponse>> GetStatus(CancellationToken cancellationToken)
    {
        var coupleId = GetAuthenticatedCoupleId();
        var result = await _statusQueryHandler.HandleAsync(
            new GetIntegrationStatusQuery(coupleId), cancellationToken);

        return Ok(new IntegrationStatusResponse(
            result.IsActive,
            result.LastEventAtUtc,
            result.LastErrorAtUtc,
            result.LastErrorMessage,
            result.RecoveryHint,
            new IntegrationCountsDto(
                result.TotalAccepted,
                result.TotalDuplicate,
                result.TotalRejected)));
    }

    private Guid GetAuthenticatedUserId()
    {
        var claimValue = User.FindFirstValue("user_id");
        if (!Guid.TryParse(claimValue, out var userId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired session.");
        return userId;
    }

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired couple context.");
        return coupleId;
    }
}
