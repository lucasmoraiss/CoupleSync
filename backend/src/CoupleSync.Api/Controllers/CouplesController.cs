using System.Security.Claims;
using CoupleSync.Api.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using CoupleSync.Api.Contracts.Couple;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Couples;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/couples")]
public sealed class CouplesController : ControllerBase
{
    private readonly CreateCoupleCommandHandler _createCoupleHandler;
    private readonly JoinCoupleCommandHandler _joinCoupleHandler;
    private readonly GetCoupleMeQueryHandler _getCoupleMeHandler;
    private readonly LeaveCoupleCommandHandler _leaveCoupleHandler;
    private readonly RemoveCoupleMemberCommandHandler _removeMemberHandler;
    private readonly RegenerateJoinCodeCommandHandler _regenerateJoinCodeHandler;
    private readonly SwitchCoupleCommandHandler _switchCoupleHandler;
    private readonly GetMyGroupsQueryHandler _getMyGroupsHandler;

    public CouplesController(
        CreateCoupleCommandHandler createCoupleHandler,
        JoinCoupleCommandHandler joinCoupleHandler,
        GetCoupleMeQueryHandler getCoupleMeHandler,
        LeaveCoupleCommandHandler leaveCoupleHandler,
        RemoveCoupleMemberCommandHandler removeMemberHandler,
        RegenerateJoinCodeCommandHandler regenerateJoinCodeHandler,
        SwitchCoupleCommandHandler switchCoupleHandler,
        GetMyGroupsQueryHandler getMyGroupsHandler)
    {
        _switchCoupleHandler = switchCoupleHandler;
        _getMyGroupsHandler = getMyGroupsHandler;
        _leaveCoupleHandler = leaveCoupleHandler;
        _removeMemberHandler = removeMemberHandler;
        _regenerateJoinCodeHandler = regenerateJoinCodeHandler;
        _createCoupleHandler = createCoupleHandler;
        _joinCoupleHandler = joinCoupleHandler;
        _getCoupleMeHandler = getCoupleMeHandler;
    }

    /// <summary>The caller's own groups (never anyone else's) and which one is active.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(MyGroupsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MyGroupsResponse>> MyGroups(CancellationToken cancellationToken)
    {
        var result = await _getMyGroupsHandler.HandleAsync(
            new GetMyGroupsQuery(GetAuthenticatedUserId()),
            cancellationToken);

        return Ok(new MyGroupsResponse(
            result.ActiveCoupleId,
            result.MaxGroups,
            result.Groups
                .Select(g => new MyGroupResponse(
                    g.CoupleId,
                    g.Name,
                    g.IsOwner,
                    g.IsActive,
                    g.JoinedAtUtc,
                    g.Members.Select(m => new MyGroupMemberResponse(m.UserId, m.Name)).ToArray()))
                .ToArray()));
    }

    /// <summary>Makes another of the caller's groups the active one; answers with tokens for it.</summary>
    [HttpPost("switch")]
    [ProducesResponseType(typeof(SwitchCoupleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SwitchCoupleResponse>> Switch([FromBody] SwitchCoupleRequest request, CancellationToken cancellationToken)
    {
        var result = await _switchCoupleHandler.HandleAsync(
            new SwitchCoupleCommand(GetAuthenticatedUserId(), request.CoupleId),
            cancellationToken);

        return Ok(new SwitchCoupleResponse(result.CoupleId, result.AccessToken, result.RefreshToken));
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateCoupleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CreateCoupleResponse>> Create(CancellationToken cancellationToken)
    {
        var result = await _createCoupleHandler.HandleAsync(
            new CreateCoupleCommand(GetAuthenticatedUserId()),
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, new CreateCoupleResponse(result.CoupleId, result.JoinCode, result.AccessToken, result.RefreshToken));
    }

    [HttpPost("join")]
    [EnableRateLimiting(RateLimitPolicies.CoupleJoin)]
    [ProducesResponseType(typeof(JoinCoupleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<JoinCoupleResponse>> Join([FromBody] JoinCoupleRequest request, CancellationToken cancellationToken)
    {
        var result = await _joinCoupleHandler.HandleAsync(
            new JoinCoupleCommand(GetAuthenticatedUserId(), request.JoinCode),
            cancellationToken);

        return Ok(new JoinCoupleResponse(result.CoupleId, result.Members.Select(ToMemberResponse).ToArray(), result.AccessToken, result.RefreshToken));
    }

    [HttpGet("me")]
    [ProducesResponseType(typeof(GetCoupleMeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GetCoupleMeResponse>> Me(CancellationToken cancellationToken)
    {
        var result = await _getCoupleMeHandler.HandleAsync(
            new GetCoupleMeQuery(GetAuthenticatedUserId()),
            cancellationToken);

        return Ok(new GetCoupleMeResponse(
            result.CoupleId,
            result.JoinCode,
            result.CreatedAtUtc,
            result.Members.Select(ToMemberResponse).ToArray(),
            result.OwnerUserId,
            result.JoinCodeExpiresAtUtc));
    }

    [HttpPost("leave")]
    [ProducesResponseType(typeof(LeaveCoupleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaveCoupleResponse>> Leave(CancellationToken cancellationToken)
    {
        var result = await _leaveCoupleHandler.HandleAsync(
            new LeaveCoupleCommand(GetAuthenticatedUserId()),
            cancellationToken);

        return Ok(new LeaveCoupleResponse(result.AccessToken, result.RefreshToken, result.ActiveCoupleId));
    }

    /// <summary>Leaves one specific group of the caller's (the route above leaves the active one).</summary>
    [HttpPost("{coupleId:guid}/leave")]
    [ProducesResponseType(typeof(LeaveCoupleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LeaveCoupleResponse>> LeaveGroup(Guid coupleId, CancellationToken cancellationToken)
    {
        var result = await _leaveCoupleHandler.HandleAsync(
            new LeaveCoupleCommand(GetAuthenticatedUserId(), coupleId),
            cancellationToken);

        return Ok(new LeaveCoupleResponse(result.AccessToken, result.RefreshToken, result.ActiveCoupleId));
    }

    [HttpDelete("members/{memberUserId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMember(Guid memberUserId, CancellationToken cancellationToken)
    {
        await _removeMemberHandler.HandleAsync(
            new RemoveCoupleMemberCommand(GetAuthenticatedUserId(), memberUserId),
            cancellationToken);

        return NoContent();
    }

    [HttpPost("join-code")]
    [ProducesResponseType(typeof(RegenerateJoinCodeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegenerateJoinCodeResponse>> RegenerateJoinCode(CancellationToken cancellationToken)
    {
        var result = await _regenerateJoinCodeHandler.HandleAsync(
            new RegenerateJoinCodeCommand(GetAuthenticatedUserId()),
            cancellationToken);

        return Ok(new RegenerateJoinCodeResponse(result.JoinCode, result.JoinCodeExpiresAtUtc));
    }

    private Guid GetAuthenticatedUserId()
    {
        var claimValue = User.FindFirstValue("user_id");

        if (!Guid.TryParse(claimValue, out var userId))
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        return userId;
    }

    private static CoupleMemberResponse ToMemberResponse(CoupleMemberDto member)
    {
        return new CoupleMemberResponse(member.UserId, member.Name, member.Email);
    }
}