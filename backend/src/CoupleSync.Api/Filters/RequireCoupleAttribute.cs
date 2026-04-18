using CoupleSync.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.Api.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireCoupleAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User?.Identity?.IsAuthenticated != true)
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                code = "UNAUTHORIZED",
                message = "Authentication required.",
                traceId = context.HttpContext.TraceIdentifier
            });
            return Task.CompletedTask;
        }

        var coupleContext = context.HttpContext.RequestServices.GetRequiredService<ICoupleContext>();

        if (coupleContext.CoupleId is null)
        {
            context.Result = new ObjectResult(new
            {
                code = "COUPLE_REQUIRED",
                message = "You must be paired with a partner to access this resource.",
                traceId = context.HttpContext.TraceIdentifier
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        return Task.CompletedTask;
    }
}
