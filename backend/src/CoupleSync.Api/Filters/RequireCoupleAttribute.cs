using CoupleSync.Api.Errors;
using CoupleSync.Application.Common.Interfaces;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.Api.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireCoupleAttribute : Attribute, IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User?.Identity?.IsAuthenticated != true)
        {
            context.Result = ApiErrors.Result(
                context.HttpContext,
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.Unauthorized,
                "Autenticação necessária. Entre novamente.");
            return;
        }

        var coupleContext = context.HttpContext.RequestServices.GetRequiredService<ICoupleContext>();

        if (coupleContext.CoupleId is null)
        {
            context.Result = ApiErrors.Result(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.CoupleRequired,
                "Você precisa estar conectado ao seu parceiro para acessar este recurso.");
            return;
        }

        // The couple_id claim only says which group the token was issued for. A member who left or was
        // removed still holds a valid token for up to its lifetime, so membership is confirmed against the
        // database on every request (one primary-key lookup, no cache: a removal applies immediately).
        var membership = context.HttpContext.RequestServices.GetRequiredService<ICoupleMembership>();
        var isMember = Guid.TryParse(context.HttpContext.User.FindFirstValue("user_id"), out var userId)
            && await membership.IsMemberAsync(userId, coupleContext.CoupleId.Value, context.HttpContext.RequestAborted);

        if (!isMember)
        {
            context.Result = ApiErrors.Result(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.CoupleRequired,
                "Você não faz mais parte deste grupo.");
        }
    }
}
