using System.Security.Claims;
using CoupleSync.Api.Filters;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Notification.Commands;
using CoupleSync.Application.Notification.Queries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1")]
public sealed class NotificationsController : ControllerBase
{
    private readonly RegisterDeviceTokenCommandHandler _registerTokenHandler;
    private readonly GetNotificationSettingsQueryHandler _getSettingsHandler;
    private readonly UpdateNotificationSettingsCommandHandler _updateSettingsHandler;

    public NotificationsController(
        RegisterDeviceTokenCommandHandler registerTokenHandler,
        GetNotificationSettingsQueryHandler getSettingsHandler,
        UpdateNotificationSettingsCommandHandler updateSettingsHandler)
    {
        _registerTokenHandler = registerTokenHandler;
        _getSettingsHandler = getSettingsHandler;
        _updateSettingsHandler = updateSettingsHandler;
    }

    [HttpPost("devices/token")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RegisterDeviceToken(
        [FromBody] RegisterDeviceTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 512)
            return BadRequest(new { code = "INVALID_TOKEN", message = "Token must be non-empty and at most 512 characters." });

        if (!string.Equals(request.Platform, "android", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { code = "UNSUPPORTED_PLATFORM", message = "Only 'android' platform is supported." });

        var userId = GetAuthenticatedUserId();
        var coupleId = GetAuthenticatedCoupleId();

        await _registerTokenHandler.HandleAsync(
            new RegisterDeviceTokenCommand(userId, coupleId, request.Token),
            cancellationToken);

        return NoContent();
    }

    [HttpGet("notifications/settings")]
    [ProducesResponseType(typeof(NotificationSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<NotificationSettingsResponse>> GetNotificationSettings(
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();
        var coupleId = GetAuthenticatedCoupleId();

        var dto = await _getSettingsHandler.HandleAsync(
            new GetNotificationSettingsQuery(userId, coupleId),
            cancellationToken);

        return Ok(new NotificationSettingsResponse(
            dto.UserId,
            dto.LowBalanceEnabled,
            dto.LargeTransactionEnabled,
            dto.BillReminderEnabled,
            dto.UpdatedAtUtc));
    }

    [HttpPut("notifications/settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateNotificationSettings(
        [FromBody] UpdateNotificationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();
        var coupleId = GetAuthenticatedCoupleId();

        await _updateSettingsHandler.HandleAsync(
            new UpdateNotificationSettingsCommand(
                userId,
                coupleId,
                request.LowBalanceEnabled,
                request.LargeTransactionEnabled,
                request.BillReminderEnabled),
            cancellationToken);

        return NoContent();
    }

    private Guid GetAuthenticatedUserId()
    {
        var claimValue = User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? User.FindFirstValue("sub");
        if (!Guid.TryParse(claimValue, out var userId))
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid user context.");
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

public sealed record RegisterDeviceTokenRequest(string Token, string Platform);
public sealed record UpdateNotificationSettingsRequest(bool? LowBalanceEnabled, bool? LargeTransactionEnabled, bool? BillReminderEnabled);
public sealed record NotificationSettingsResponse(Guid UserId, bool LowBalanceEnabled, bool LargeTransactionEnabled, bool BillReminderEnabled, DateTime UpdatedAtUtc);
