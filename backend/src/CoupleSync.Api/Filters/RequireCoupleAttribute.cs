using CoupleSync.Api.Errors;
using CoupleSync.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http;
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
            context.Result = ApiErrors.Result(
                context.HttpContext,
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.Unauthorized,
                "Autenticação necessária. Entre novamente.");
            return Task.CompletedTask;
        }

        var coupleContext = context.HttpContext.RequestServices.GetRequiredService<ICoupleContext>();

        if (coupleContext.CoupleId is null)
        {
            context.Result = ApiErrors.Result(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.CoupleRequired,
                "Você precisa estar conectado ao seu parceiro para acessar este recurso.");
        }

        return Task.CompletedTask;
    }
}
