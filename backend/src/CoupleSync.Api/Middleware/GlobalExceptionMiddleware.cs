using System.Reflection;
using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using FluentValidation;

namespace CoupleSync.Api.Middleware;

public sealed class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Assembly DomainAssembly = typeof(CoupleSync.Domain.Entities.Transaction).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(AppException).Assembly;

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", ex.Errors.FirstOrDefault()?.ErrorMessage ?? "Validation failed.");
        }
        catch (AppException ex)
        {
            await WriteErrorAsync(context, ex.StatusCode, ex.Code, ex.Message);
        }
        catch (ArgumentException ex) when (IsBusinessRuleViolation(ex))
        {
            // Safety net: invariants enforced by the domain entities / application services signal
            // invalid client input. Argument exceptions raised anywhere else (BCL, EF Core, drivers)
            // are genuine server errors and fall through to the 500 handler below.
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", StripParameterSuffix(ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception while processing request.");
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "INTERNAL_SERVER_ERROR", "An unexpected error occurred.");
        }
    }

    private static bool IsBusinessRuleViolation(ArgumentException ex)
    {
        var origin = ex.TargetSite?.DeclaringType?.Assembly;
        return origin is not null && (origin == DomainAssembly || origin == ApplicationAssembly);
    }

    /// <summary>ArgumentException.Message appends " (Parameter 'x')"; clients only need the rule text.</summary>
    private static string StripParameterSuffix(ArgumentException ex)
    {
        var message = ex.Message;
        var suffixStart = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        return suffixStart > 0 ? message[..suffixStart] : message;
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var payload = new ErrorEnvelope(code, message, context.TraceIdentifier);
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private sealed record ErrorEnvelope(string Code, string Message, string TraceId);
}
