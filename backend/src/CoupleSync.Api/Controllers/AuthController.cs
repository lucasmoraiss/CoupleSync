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
    private readonly RequestPasswordResetCommandHandler _requestPasswordResetHandler;
    private readonly ResetPasswordCommandHandler _resetPasswordHandler;
    private readonly ConfirmEmailCommandHandler _confirmEmailHandler;
    private readonly ResendEmailVerificationCommandHandler _resendEmailVerificationHandler;
    private readonly GetCurrentUserQueryHandler _currentUserHandler;

    public AuthController(
        RegisterCommandHandler registerHandler,
        LoginCommandHandler loginHandler,
        RefreshTokenCommandHandler refreshTokenHandler,
        LogoutCommandHandler logoutHandler,
        ChangePasswordCommandHandler changePasswordHandler,
        RequestPasswordResetCommandHandler requestPasswordResetHandler,
        ResetPasswordCommandHandler resetPasswordHandler,
        ConfirmEmailCommandHandler confirmEmailHandler,
        ResendEmailVerificationCommandHandler resendEmailVerificationHandler,
        GetCurrentUserQueryHandler currentUserHandler)
    {
        _requestPasswordResetHandler = requestPasswordResetHandler;
        _resetPasswordHandler = resetPasswordHandler;
        _confirmEmailHandler = confirmEmailHandler;
        _resendEmailVerificationHandler = resendEmailVerificationHandler;
        _currentUserHandler = currentUserHandler;
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
    [EnableRateLimiting(RateLimitPolicies.AuthRefresh)]
    [ProducesResponseType(typeof(RefreshResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
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
        var result = await _changePasswordHandler.HandleAsync(
            new ChangePasswordCommand(RequireUserId(), request.CurrentPassword, request.NewPassword),
            cancellationToken);

        return Ok(new RefreshResponse(result.AccessToken, result.RefreshToken));
    }

    /// <summary>The signed-in user, including whether the e-mail was confirmed.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(CurrentUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CurrentUserResponse>> Me(CancellationToken cancellationToken)
    {
        var user = await _currentUserHandler.HandleAsync(RequireUserId(), cancellationToken);
        return Ok(new CurrentUserResponse(user.Id, user.Email, user.Name, user.EmailVerified));
    }

    /// <summary>
    /// Sends a password-reset code by e-mail. 200 with the same body for any address, registered or not.
    /// 503 EMAIL_NOT_CONFIGURED while sending is off.
    /// </summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting(RateLimitPolicies.AuthForgotPassword)]
    [ProducesResponseType(typeof(ForgotPasswordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ForgotPasswordResponse>> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        await _requestPasswordResetHandler.HandleAsync(new RequestPasswordResetCommand(request.Email), cancellationToken);

        return Ok(new ForgotPasswordResponse("Se o e-mail estiver cadastrado, enviaremos um código para redefinir a senha."));
    }

    /// <summary>Sets a new password with the e-mailed code and ends every session of the user. 400 INVALID_CODE when the code does not work.</summary>
    [HttpPost("reset-password")]
    [EnableRateLimiting(RateLimitPolicies.AuthResetPassword)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await _resetPasswordHandler.HandleAsync(
            new ResetPasswordCommand(request.Email, request.Code, request.NewPassword),
            cancellationToken);

        return NoContent();
    }

    /// <summary>Confirms the signed-in user e-mail with the code sent at sign-up. Accounts that never confirm keep working.</summary>
    [HttpPost("confirm-email")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.AuthConfirmEmail)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest request, CancellationToken cancellationToken)
    {
        await _confirmEmailHandler.HandleAsync(new ConfirmEmailCommand(RequireUserId(), request.Code), cancellationToken);

        return NoContent();
    }

    /// <summary>Sends a new confirmation code; the previous one stops working.</summary>
    [HttpPost("resend-email-verification")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.AuthResendEmailVerification)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResendEmailVerification(CancellationToken cancellationToken)
    {
        await _resendEmailVerificationHandler.HandleAsync(new ResendEmailVerificationCommand(RequireUserId()), cancellationToken);

        return NoContent();
    }

    private Guid RequireUserId()
    {
        if (!Guid.TryParse(User.FindFirstValue("user_id"), out var userId))
        {
            throw new UnauthorizedException("UNAUTHORIZED", "Sessão inválida ou expirada. Entre novamente.");
        }

        return userId;
    }

    private static AuthResponse ToAuthResponse(AuthResult result)
    {
        return new AuthResponse(
            new AuthUserResponse(result.User.Id, result.User.Email, result.User.Name, result.User.EmailVerified),
            result.AccessToken,
            result.RefreshToken);
    }
}
