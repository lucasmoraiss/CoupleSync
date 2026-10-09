using System.Globalization;
using System.Security.Claims;
using CoupleSync.Api.Contracts.Ai;
using CoupleSync.Api.Filters;
using CoupleSync.Application.AiFacts;
using CoupleSync.Application.Common.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

/// <summary>
/// "Assinaturas e recorrências" of the group of the token (design 10.3): what repeats in its transactions, found by
/// code (no AI, so it answers whether or not the group switched the AI on), and what the person corrects.
/// </summary>
[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/ai/recurring")]
public sealed class RecurringController : ControllerBase
{
    private readonly RecurrenceService _service;

    public RecurringController(RecurrenceService service)
    {
        _service = service;
    }

    /// <summary>The list. Calculates again first when the last calculation is old or a transaction came in after it.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(RecurringListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RecurringListResponse>> Get(CancellationToken ct)
    {
        var list = await _service.GetAsync(GetAuthenticatedCoupleId(), ct);
        return Ok(new RecurringListResponse(
            list.MonthlyTotal,
            list.AnnualTotal,
            list.DetectedAtUtc,
            list.Subscriptions.Select(Map).ToList(),
            list.FixedBills.Select(Map).ToList(),
            list.Installments.Select(Map).ToList(),
            list.Habits.Select(Map).ToList(),
            list.Hidden.Select(Map).ToList(),
            list.InstallmentsByMonth.Select(m => new RecurringMonthCommitmentResponse(m.Month, m.Amount)).ToList()));
    }

    [HttpGet("{id:guid}/transactions")]
    [ProducesResponseType(typeof(RecurringChargesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringChargesResponse>> GetTransactions(Guid id, CancellationToken ct)
    {
        var charges = await _service.GetChargesAsync(GetAuthenticatedCoupleId(), id, ct);
        return Ok(new RecurringChargesResponse(
            charges.Select(c => new RecurringChargeResponse(c.TransactionId, c.TimestampUtc, c.Amount, c.Name)).ToList()));
    }

    /// <summary>What the person says about the stream. It survives every recalculation.</summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(RecurringItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RecurringItemResponse>> SetOverride(Guid id, [FromBody] RecurringOverrideRequest request, CancellationToken ct)
        => Ok(Map(await _service.SetOverrideAsync(GetAuthenticatedCoupleId(), GetAuthenticatedUserId(), id, request.Override, ct)));

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static RecurringItemResponse Map(RecurringItem item) => new(
        item.Id,
        item.Name,
        item.Kind,
        item.VariableAmount,
        item.Cadence,
        item.Amount,
        item.LastAmount,
        item.PreviousAmount,
        item.AnnualCost,
        item.Occurrences,
        item.MissedCount,
        Date(item.FirstSeen),
        Date(item.LastSeen),
        item.NextExpected is { } next ? Date(next) : null,
        item.Status,
        item.Flags,
        item.Confidence,
        item.Category,
        item.Person is null ? null : new RecurringPersonResponse(item.Person.UserId, item.Person.Name),
        item.Installment is null ? null : new RecurringInstallmentResponse(item.Installment.Number, item.Installment.Total, item.Installment.RemainingAmount, item.Installment.EndMonth),
        item.Override);

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
