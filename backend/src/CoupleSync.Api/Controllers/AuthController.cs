using CoupleSync.Api.Contracts.Auth;
using CoupleSync.Api.RateLimiting;
using System.Security.Claims;
using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly RegisterCommandHandler _registerHandler;
    private readonly LoginCommandHandler _loginHandler;
    private readonly RefreshTokenCommandHandler _refreshTokenHandler;
    private readonly LogoutCommandHandler _logoutHandler;
    private readonly ChangePasswordCommandHandler _changePasswordHandler;

    public AuthController(
        RegisterCommandHandler registerHandler,
        LoginCommandHandler loginHandler,
        RefreshTokenCommandHandler refreshTokenHandler,
        LogoutCommandHandler logoutHandler,
        ChangePasswordCommandHandler changePasswordHandler)
    {
        _logoutHandler = logoutHandler;
        _changePasswordHandler = changePasswordHandler;
        _registerHandler = registerHandler;
        _loginHandler = loginHandler;
        _refreshTokenHandler = refreshTokenHandler;
    }

    [HttpPost("register")]
    [EnableRateLimiting(RateLimitPolicies.AuthRegister)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
       var result = await _registerHandler.HandleAsync(
            new RegisterCommand(request.Email, request.Name, request.Password),
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, ToAuthResponse(result));
    }

    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicies.AuthLogin)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await _loginHandler.HandleAsync(
            new LoginCommand(request.Email, request.Password),
            cancellationToken);

        return Ok(ToAuthResponse(result));
    }

    [HttpPost("refresh")]
    [ProducesResponseType(typeof(RefreshResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RefreshResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        var result = await _refreshTokenHandler.HandleAsync(
            new RefreshTokenCommand(request.RefreshToken),
            cancellationToken);

        return Ok(new RefreshResponse(result.AccessToken, result.RefreshToken));
    }

    /// <summary>
    /// Revokes the refresh token. Needs no access token: the caller may be signing out precisely because it
    /// expired. Always 204, whether or not the token was known. One refresh token exists per user, so this
    /// also ends the session on every other device.
    /// </summary>
    [HttpPost("logout")]
    [EnableRateLimiting(RateLimitPolicies.AuthLogout)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken cancellationToken)
    {
        await _logoutHandler.HandleAsync(new LogoutCommand(request.RefreshToken, request.DeviceToken), cancellationToken);

        return NoContent();
    }

    /// <summary>
    /// Changes the password of the signed-in user (needs the current one) and replaces every refresh token
    /// with a new one returned here, so other devices are signed out and this one keeps going.
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.AuthChangePassword)]
    [ProducesResponseType(typeof(RefreshResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RefreshResponse>> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("user_id"), out var userId))
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        var result = await _changePasswordHandler.HandleAsync(
            new ChangePasswordCommand(userId, request.CurrentPassword, request.NewPassword),
            cancellationToken);

        return Ok(new RefreshResponse(result.AccessToken, result.RefreshToken));
    }

    private static AuthResponse ToAuthResponse(AuthResult result)
    {
        return new AuthResponse(
            new AuthUserResponse(result.User.Id, result.User.Email, result.User.Name),
            result.AccessToken,
            result.RefreshToken);
    }
}
