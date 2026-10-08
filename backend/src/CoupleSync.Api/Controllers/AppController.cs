using CoupleSync.Api.Contracts.AppUpdate;
using CoupleSync.Api.RateLimiting;
using CoupleSync.Application.AppUpdate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoupleSync.Api.Controllers;

/// <summary>
/// What the installed app asks about itself. Anonymous: the app asks whoever is (or is not) signed in, and the
/// answer says nothing about any user.
/// </summary>
[ApiController]
[Route("api/v1/app")]
public sealed class AppController : ControllerBase
{
    private readonly AppVersionService _versions;

    public AppController(AppVersionService versions)
    {
        _versions = versions;
    }

    /// <summary>
    /// The latest published APK version, the oldest one still accepted and where to download. Always 200: an unknown
    /// version is null, and the app then shows nothing.
    /// </summary>
    [HttpGet("version"), AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.AppVersion)]
    [ProducesResponseType(typeof(AppVersionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AppVersionResponse>> GetVersion(CancellationToken ct)
    {
        var info = await _versions.GetAsync(ct);
        return Ok(new AppVersionResponse(info.LatestVersion, info.MinimumVersion, info.DownloadUrl));
    }
}
