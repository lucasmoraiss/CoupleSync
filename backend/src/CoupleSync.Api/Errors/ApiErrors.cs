using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace CoupleSync.Api.Errors;

/// <summary>
/// Single entry point to build/write the API error format. Prefer throwing an <c>AppException</c>
/// (NotFound/Conflict/BadRequest/...) from handlers and controllers; use these helpers only where an
/// exception cannot be used (filters, rate limiter, model-state factory).
/// </summary>
public static class ApiErrors
{
    public const string UnexpectedErrorMessage = "Ocorreu um erro inesperado. Tente novamente em instantes.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ApiErrorResponse Create(
        HttpContext context,
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? errors = null)
        => new(code, message, errors, context.TraceIdentifier);

    /// <summary>Result for MVC filters and controllers.</summary>
    public static ObjectResult Result(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? errors = null)
        => new(Create(context, code, message, errors))
        {
            StatusCode = statusCode,
            ContentTypes = { "application/json" }
        };

    /// <summary>Writes the error straight to the response (middleware, rate limiter).</summary>
    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? errors = null,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var payload = Create(context, code, message, errors);
        await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
    }

    /// <summary>Code and message for responses that reached the pipeline end without a body.</summary>
    public static (string Code, string Message) DescribeStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => (ApiErrorCodes.BadRequest, "Requisição inválida."),
        StatusCodes.Status401Unauthorized => (ApiErrorCodes.Unauthorized, "Sua sessão expirou ou é inválida. Entre novamente."),
        StatusCodes.Status403Forbidden => (ApiErrorCodes.Forbidden, "Você não tem permissão para acessar este recurso."),
        StatusCodes.Status404NotFound => (ApiErrorCodes.NotFound, "Recurso não encontrado."),
        StatusCodes.Status405MethodNotAllowed => (ApiErrorCodes.MethodNotAllowed, "Operação não permitida para este recurso."),
        StatusCodes.Status413PayloadTooLarge => (ApiErrorCodes.PayloadTooLarge, "O conteúdo enviado é grande demais."),
        StatusCodes.Status415UnsupportedMediaType => (ApiErrorCodes.UnsupportedMediaType, "Formato de conteúdo não suportado."),
        StatusCodes.Status429TooManyRequests => (ApiErrorCodes.RateLimitExceeded, "Muitas tentativas. Aguarde um instante e tente novamente."),
        >= 500 => (ApiErrorCodes.InternalServerError, UnexpectedErrorMessage),
        _ => (ApiErrorCodes.BadRequest, "Não foi possível processar a requisição.")
    };
}
