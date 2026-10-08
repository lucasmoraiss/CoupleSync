using System.Globalization;
using System.Security.Claims;
using CoupleSync.Api.Contracts.Ai;
using CoupleSync.Api.Filters;
using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

/// <summary>
/// Switching the AI analysis on and off for the group of the token, the person's own choices about it and what the
/// group consumed (design 10.2). One acceptance is enough for the group; any member switches it off.
/// </summary>
[ApiController]
[Authorize]
[RequireCouple]
[Route("api/v1/ai")]
public sealed class AiController : ControllerBase
{
    private readonly AiActivationService _service;

    public AiController(AiActivationService service)
    {
        _service = service;
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(AiStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AiStatusResponse>> GetStatus(CancellationToken ct)
        => Ok(Map(await _service.GetStatusAsync(GetAuthenticatedCoupleId(), GetAuthenticatedUserId(), ct)));

    /// <summary>Switches the AI on for the group. 409 when the version sent is not the one in force.</summary>
    [HttpPost("consent")]
    [ProducesResponseType(typeof(AiStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AiStatusResponse>> Accept([FromBody] AiConsentRequest request, CancellationToken ct)
        => Ok(Map(await _service.AcceptAsync(GetAuthenticatedCoupleId(), GetAuthenticatedUserId(), request.Version, ct)));

    /// <summary>scope=mine takes back the person's own acceptance; scope=group switches the AI off for everyone.</summary>
    [HttpDelete("consent")]
    [ProducesResponseType(typeof(AiStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AiStatusResponse>> Revoke([FromQuery] string? scope, CancellationToken ct)
        => Ok(Map(await _service.RevokeAsync(GetAuthenticatedCoupleId(), GetAuthenticatedUserId(), scope, ct)));

    [HttpPatch("preferences")]
    [ProducesResponseType(typeof(AiStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AiStatusResponse>> UpdatePreferences([FromBody] AiPreferencesRequest request, CancellationToken ct)
        => Ok(Map(await _service.UpdatePreferencesAsync(
            GetAuthenticatedCoupleId(), GetAuthenticatedUserId(), request.WeeklyEmail, request.OnboardingAnswered, ct)));

    /// <summary>What the group of the token consumed in the last <paramref name="days"/> Brasília days (1 to 90).</summary>
    [HttpGet("usage")]
    [ProducesResponseType(typeof(AiUsageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AiUsageResponse>> GetUsage([FromQuery] string? days, CancellationToken ct)
    {
        // Read by hand so that "abc", "0" and "91" all get the same answer in the single error format.
        var parsed = 30;
        if (days is not null && !int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
            throw new BadRequestException("INVALID_DAYS", "Informe um número de dias entre 1 e 90.");

        var usage = await _service.GetUsageAsync(GetAuthenticatedCoupleId(), parsed, ct);
        return Ok(new AiUsageResponse(
            usage.Days.Select(d => new AiUsageDayResponse(
                d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Calls, d.InputTokens, d.OutputTokens, d.Failures)).ToList(),
            usage.ByFeature.Select(f => new AiUsageFeatureResponse(f.Feature, f.Calls, f.InputTokens, f.OutputTokens)).ToList(),
            usage.ProvidersToday.Select(p => new AiUsageProviderResponse(p.Name, p.Model, p.Calls, p.Limit, p.PercentUsed, p.ExhaustedToday)).ToList(),
            new AiGroupBudgetResponse(
                usage.GroupBudget.CallsToday, usage.GroupBudget.CallLimit, usage.GroupBudget.TokensToday, usage.GroupBudget.TokenLimit, usage.GroupBudget.ResetsAtLocal)));
    }

    private static AiStatusResponse Map(AiStatus status) => new(
        status.Available,
        status.Enabled,
        status.ConsentVersion,
        status.AcceptedBy.Select(a => new AiAcceptedByResponse(a.UserId, a.Name, a.AcceptedAtUtc)).ToList(),
        status.MyAcceptance is null ? null : new AiMyAcceptanceResponse(status.MyAcceptance.AcceptedAtUtc),
        status.OnboardingPending,
        status.WeeklyEmailEnabled,
        status.EmailVerified,
        status.EmailConfigured,
        status.Providers.Select(p => new AiProviderResponse(p.Name, p.Country, p.TrainsOnData)).ToList(),
        new AiFeaturesResponse(status.Features.Assistant, status.Features.Insights, status.Features.Education, status.Features.WeeklyEmail),
        new AiBudgetResponse(status.Budget.CallsToday, status.Budget.CallLimit, status.Budget.ResetsAtLocal));

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
