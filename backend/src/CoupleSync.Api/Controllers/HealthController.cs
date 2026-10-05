using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private const int ShortCommitLength = 7;

    private readonly IConfiguration _configuration;

    public HealthController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet, AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetHealth()
    {
        return Ok(new
        {
            status = "healthy",
            version = GetDeployedVersion(),
            timestamp = DateTime.UtcNow
        });
    }

    // APP_VERSION when set explicitly; otherwise the commit the hosting platform built (Render).
    private string GetDeployedVersion()
    {
        var version = _configuration["APP_VERSION"];
        if (string.IsNullOrWhiteSpace(version))
            version = _configuration["RENDER_GIT_COMMIT"];
        if (string.IsNullOrWhiteSpace(version))
            return "unknown";

        version = version.Trim();
        return version.Length > ShortCommitLength ? version[..ShortCommitLength] : version;
    }
}
