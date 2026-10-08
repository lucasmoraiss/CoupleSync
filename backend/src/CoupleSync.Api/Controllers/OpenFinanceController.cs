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
/// Open Finance through Meu Pluggy: credentials, items and the accounts found (phase 1); synchronisation of the
/// transactions into a mirror and the review that turns expenses into transactions (phase 2). The whole group reads
/// and reviews; only who connected changes the connection and asks for a synchronisation. Without
/// OPENFINANCE_ENCRYPTION_KEY on the server every write of the connection answers 503 OPENFINANCE_UNAVAILABLE and
/// the status says <c>available: false</c>.
/// </summary>
[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/openfinance")]
public sealed class OpenFinanceController : ControllerBase
{
    private readonly OpenFinanceService _service;
    private readonly SyncRunService _syncRuns;
    private readonly BankReviewService _review;

    public OpenFinanceController(OpenFinanceService service, SyncRunService syncRuns, BankReviewService review)
    {
        _service = service;
        _syncRuns = syncRuns;
        _review = review;
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
    [EnableRateLimiting(RateLimitPolicies.OpenFinanceItems)]
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

    /// <summary>
    /// Enqueues a synchronisation of the caller's own connection and answers at once; the run is followed by
    /// <c>GET sync-runs/{id}</c>. <c>force=true</c> asks Pluggy to read the banks again first (the manual button).
    /// <c>appOpen=true</c> marks the silent request the app makes when it is opened (it never forces).
    /// <c>aiCategorizationConsent=true</c> says who connected accepted the AI disclosure: only then may a description
    /// the category table does not know go to the AI classifier. <c>historyMonths</c> (3, 6 or 12), when given,
    /// becomes the period of the connection: how far back an account never read before goes. While a run of the
    /// connection is waiting or running: 409 SYNC_ALREADY_RUNNING. Otherwise one run per connection every 10 minutes
    /// (409 SYNC_TOO_SOON, with <c>errors.nextSyncAtUtc</c>).
    /// </summary>
    [HttpPost("connections/{id:guid}/sync")]
    [EnableRateLimiting(RateLimitPolicies.OpenFinanceSync)]
    [ProducesResponseType(typeof(SyncRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SyncRunResponse>> RequestSync(
        Guid id,
        [FromQuery] bool force = false,
        [FromQuery] bool appOpen = false,
        [FromQuery] bool aiCategorizationConsent = false,
        [FromQuery] int? historyMonths = null,
        CancellationToken ct = default)
    {
        var run = await _syncRuns.RequestAsync(
            GetAuthenticatedCoupleId(), GetAuthenticatedUserId(), id, force, appOpen, aiCategorizationConsent, historyMonths, ct);
        return StatusCode(StatusCodes.Status202Accepted, Map(run));
    }

    [HttpGet("sync-runs/{id:guid}")]
    [ProducesResponseType(typeof(SyncRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SyncRunResponse>> GetSyncRun(Guid id, CancellationToken ct)
        => Ok(Map(await _syncRuns.GetAsync(GetAuthenticatedCoupleId(), id, ct)));

    /// <summary>The expenses of a month of Brazil (<c>month=AAAA-MM</c>; the current one when absent) waiting for the review, and the discarded ones apart.</summary>
    [HttpGet("review")]
    [ProducesResponseType(typeof(BankReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BankReviewResponse>> GetReview([FromQuery] string? month, CancellationToken ct)
    {
        var review = await _review.GetReviewAsync(GetAuthenticatedCoupleId(), month, ct);
        return Ok(new BankReviewResponse(
            review.Month,
            review.Expenses.Select(Map).ToList(),
            review.Discarded.Select(Map).ToList(),
            review.PendingTotalBrl,
            review.PendingAllMonths,
            review.PendingByMonth.Select(m => new BankReviewMonthResponse(m.Month, m.Pending)).ToList()));
    }

    /// <summary>
    /// Turns the chosen expenses into transactions (of who connected the account) and discards the others. All or
    /// nothing. A line not settled at the bank yet answers 422 TRANSACTION_NOT_POSTED; a line of another group, 404.
    /// A line without a value, or in a currency that is not BRL, is not an error: it is left waiting and listed in <c>skipped</c>.
    /// </summary>
    [HttpPost("review/confirm")]
    [ProducesResponseType(typeof(ConfirmBankReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ConfirmBankReviewResponse>> ConfirmReview([FromBody] ConfirmBankReviewRequest request, CancellationToken ct)
    {
        var result = await _review.ConfirmAsync(
            GetAuthenticatedCoupleId(),
            request.Expenses?.Select(e => new ConfirmExpenseInput(e.Id, e.Category, e.Description)).ToList(),
            request.Discard,
            ct);
        return Ok(new ConfirmBankReviewResponse(
            result.Created.Select(c => new BankReviewCreatedResponse(c.Id, c.TransactionId)).ToList(),
            result.Discarded,
            result.AlreadyConfirmed,
            result.Skipped,
            result.SkippedOtherCurrency));
    }

    /// <summary>Discarded lines go back to waiting for the review.</summary>
    [HttpPost("review/restore")]
    [ProducesResponseType(typeof(RestoreBankReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RestoreBankReviewResponse>> RestoreReview([FromBody] IReadOnlyList<Guid> ids, CancellationToken ct)
        => Ok(new RestoreBankReviewResponse(await _review.RestoreAsync(GetAuthenticatedCoupleId(), ids, ct)));

    private static SyncRunResponse Map(SyncRunDto r) => new(
        r.Id, r.ConnectionId, r.Status, r.TriggeredBy, r.CreatedAtUtc, r.StartedAtUtc, r.FinishedAtUtc,
        r.TransactionsNew, r.TransactionsUpdated, r.ErrorCode, r.ErrorMessage);

    private static BankReviewLineResponse Map(BankReviewLineDto l) => new(
        l.Id, l.Day, l.Merchant, l.Description, l.Amount, l.Currency, l.SuggestedCategory, l.BankStatus,
        l.BankName, l.AccountName, l.InstallmentNumber, l.InstallmentTotal);

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
