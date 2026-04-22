using System.Security.Claims;
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

    public CouplesController(
        CreateCoupleCommandHandler createCoupleHandler,
        JoinCoupleCommandHandler joinCoupleHandler,
        GetCoupleMeQueryHandler getCoupleMeHandler)
    {
        _createCoupleHandler = createCoupleHandler;
        _joinCoupleHandler = joinCoupleHandler;
        _getCoupleMeHandler = getCoupleMeHandler;
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

        return StatusCode(StatusCodes.Status201Created, new CreateCoupleResponse(result.CoupleId, result.JoinCode, result.AccessToken));
    }

    [HttpPost("join")]
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

        return Ok(new JoinCoupleResponse(result.CoupleId, result.Members.Select(ToMemberResponse).ToArray(), result.AccessToken));
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
            result.Members.Select(ToMemberResponse).ToArray()));
    }

    private Guid GetAuthenticatedUserId()
    {
        var claimValue = User.FindFirstValue("user_id");

        if (!Guid.TryParse(claimValue, out var userId))
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Invalid or expired session.");
        }

        return userId;
    }

    private static CoupleMemberResponse ToMemberResponse(CoupleMemberDto member)
    {
        return new CoupleMemberResponse(member.UserId, member.Name, member.Email);
    }
}