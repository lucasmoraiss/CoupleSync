using System.Reflection;
using CoupleSync.Api.Errors;
using CoupleSync.Application.Common.Exceptions;
using FluentValidation;

namespace CoupleSync.Api.Middleware;

/// <summary>
/// Last line of the single error format: exceptions and body-less 4xx/5xx responses leave here as
/// <see cref="ApiErrorResponse"/>. Detail of unexpected failures goes only to the log.
/// </summary>
public sealed class GlobalExceptionMiddleware
{
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
            await CompleteBodylessErrorAsync(context);
        }
        catch (ValidationException ex)
        {
            var errors = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
            await WriteAsync(context, StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationError,
                ex.Errors.FirstOrDefault()?.ErrorMessage ?? "Dados inválidos. Verifique os campos e tente novamente.",
                errors.Count == 0 ? null : errors);
        }
        catch (AppException ex)
        {
            await WriteAsync(context, ex.StatusCode, ex.Code, ex.Message, ex.Errors);
        }
        catch (ArgumentException ex) when (IsBusinessRuleViolation(ex))
        {
            // Safety net: invariants enforced by the domain entities / application services signal
            // invalid client input. Argument exceptions raised anywhere else (BCL, EF Core, drivers)
            // are genuine server errors and fall through to the 500 handler below.
            await WriteAsync(context, StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationError, StripParameterSuffix(ex));
        }
        catch (BadHttpRequestException ex)
        {
            // Kestrel/MVC protocol-level failures (body too large, truncated form...). The framework
            // message is English and technical, so only the status is kept.
            _logger.LogWarning(ex, "Bad HTTP request.");
            var (code, message) = ApiErrors.DescribeStatus(ex.StatusCode);
            await WriteAsync(context, ex.StatusCode, code, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception while processing request.");
            await WriteAsync(context, StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalServerError, ApiErrors.UnexpectedErrorMessage);
        }
    }

    /// <summary>
    /// 401 (JWT), 403, 404 (unknown route), 405, 415... are produced by the framework with an empty body.
    /// Give them the standard body too.
    /// </summary>
    private static async Task CompleteBodylessErrorAsync(HttpContext context)
    {
        var response = context.Response;
        if (response.HasStarted || response.StatusCode < 400) return;
        if (response.ContentLength is > 0 || !string.IsNullOrEmpty(response.ContentType)) return;

        // Headers set by the framework (WWW-Authenticate...) are kept: only the body is missing.
        var (code, message) = ApiErrors.DescribeStatus(response.StatusCode);
        await ApiErrors.WriteAsync(context, response.StatusCode, code, message);
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

    private static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        if (context.Response.HasStarted) return Task.CompletedTask;

        context.Response.Clear();
        return ApiErrors.WriteAsync(context, statusCode, code, message, errors);
    }
}
