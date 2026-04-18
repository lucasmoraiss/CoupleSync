using CoupleSync.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http;

namespace CoupleSync.Infrastructure.Security;

public sealed class HttpContextCoupleContext : ICoupleContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCoupleContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? CoupleId
    {
        get
        {
            var claimValue = _httpContextAccessor.HttpContext?.User?.FindFirst("couple_id")?.Value;
            if (string.IsNullOrEmpty(claimValue))
            {
                return null;
            }
            return Guid.TryParse(claimValue, out var coupleId) ? coupleId : null;
        }
    }
}
