using System.Security.Claims;
using CoupleSync.Api.Contracts.OpenFinance;
using CoupleSync.Api.Filters;
using CoupleSync.Api.RateLimiting;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.OpenFinance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoupleSync.Api.Controllers;

/// <summary>
/// Open Finance through Meu Pluggy, phase 1: credentials, items and the accounts found. The whole group reads;
/// only who connected writes. Without OPENFINANCE_ENCRYPTION_KEY on the server every write answers
/// 503 OPENFINANCE_UNAVAILABLE and the status says <c>available: false</c>.
/// </summary>
[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/openfinance")]
public sealed class OpenFinanceController : ControllerBase
{
    private readonly OpenFinanceService _service;

    public OpenFinanceController(OpenFinanceService service)
    {
        _service = service;
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(OpenFinanceStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OpenFinanceStatusResponse>> GetStatus(CancellationToken ct)
    {
        var status = await _service.GetStatusAsync(GetAuthenticatedCoupleId(), GetAuthenticatedUserId(), ct);
        return Ok(new OpenFinanceStatusResponse(status.Available, status.Connections.Select(Map).ToList()));
    }

    /// <summary>Asks Pluggy whether the credentials work. Stores nothing.</summary>
    [HttpPost("credentials/test")]
    [EnableRateLimiting(RateLimitPolicies.OpenFinanceCredentials)]
    [ProducesResponseType(typeof(TestCredentialsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TestCredentialsResponse>> TestCredentials([FromBody] TestCredentialsRequest request, CancellationToken ct)
    {
        await _service.TestCredentialsAsync(request.ClientId, request.ClientSecret, ct);
        return Ok(new TestCredentialsResponse(true));
    }

    /// <summary>One connection per person and group (409 otherwise). Shares the rate limit of the credentials test.</summary>
    [HttpPost("connections")]
    [EnableRateLimiting(RateLimitPolicies.OpenFinanceCredentials)]
    [ProducesResponseType(typeof(BankConnectionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BankConnectionResponse>> CreateConnection([FromBody] CreateBankConnectionRequest request, CancellationToken ct)
    {
        var connection = await _service.CreateConnectionAsync(
            GetAuthenticatedCoupleId(),
            GetAuthenticatedUserId(),
            new CreateBankConnectionInput(request.Label, request.ClientId, request.ClientSecret, request.HistoryMonths),
            ct);

        return StatusCode(StatusCodes.Status201Created, Map(connection));
    }

    /// <summary>Checks the item at Pluggy with the connection's credentials, stores it and returns the accounts found.</summary>
    [HttpPost("connections/{id:guid}/items")]
    [ProducesResponseType(typeof(BankItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BankItemResponse>> AddItem(Guid id, [FromBody] AddBankItemRequest request, CancellationToken ct)
    {
        var item = await _service.AddItemAsync(GetAuthenticatedCoupleId(), GetAuthenticatedUserId(), id, request.ItemId, ct);
        return Ok(Map(item));
    }

    [HttpPatch("accounts/{id:guid}")]
    [ProducesResponseType(typeof(BankAccountResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<BankAccountResponse>> UpdateAccount(Guid id, [FromBody] UpdateBankAccountRequest request, CancellationToken ct)
    {
        var account = await _service.SetAccountSyncAsync(
            GetAuthenticatedCoupleId(), GetAuthenticatedUserId(), id, request.SyncEnabled ?? true, ct);
        return Ok(Map(account));
    }

    /// <summary>Erases the credentials at once and marks the connection as disconnected; items and accounts stay.</summary>
    [HttpDelete("connections/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken ct)
    {
        await _service.DisconnectAsync(GetAuthenticatedCoupleId(), GetAuthenticatedUserId(), id, ct);
        return NoContent();
    }

    private static BankConnectionResponse Map(BankConnectionDto c) => new(
        c.Id, c.Label, c.UserId, c.UserName, c.IsMine, c.Status, c.ClientIdHint, c.HistoryMonths,
        c.LastSyncAtUtc, c.LastErrorCode, c.LastErrorMessage, c.CreatedAtUtc, c.Items.Select(Map).ToList());

    private static BankItemResponse Map(BankItemDto i) => new(
        i.Id, i.ConnectorName, i.Status, i.ExecutionStatus, i.LastUpdatedAtUtc, i.LastErrorMessage, i.Accounts.Select(Map).ToList());

    private static BankAccountResponse Map(BankAccountDto a) => new(
        a.Id, a.Type, a.Subtype, a.Name, a.MarketingName, a.NumberMasked, a.Currency, a.Balance, a.BalanceAtUtc,
        a.CreditLimit, a.AvailableCreditLimit, a.BalanceCloseDate, a.BalanceDueDate, a.MinimumPayment, a.Brand, a.SyncEnabled);

    private Guid GetAuthenticatedUserId()
    {
        var claimValue = User.FindFirstValue("user_id");
        if (!Guid.TryParse(claimValue, out var userId))
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        return userId;
    }

    private Guid GetAuthenticatedCoupleId()
    {
        var claimValue = User.FindFirstValue("couple_id");
        if (!Guid.TryParse(claimValue, out var coupleId))
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        return coupleId;
    }
}
