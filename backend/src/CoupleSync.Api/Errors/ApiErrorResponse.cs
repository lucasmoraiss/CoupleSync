using System.Text.Json.Serialization;

namespace CoupleSync.Api.Errors;

/// <summary>
/// The only shape of an error body (any 4xx/5xx of any route).
/// <c>code</c> is a stable UPPER_SNAKE_CASE identifier the app can branch on, <c>message</c> is
/// Brazilian Portuguese text ready to show to the user, <c>errors</c> (validation only) maps a field
/// name to its messages, and <c>traceId</c> correlates the response with the server log.
/// </summary>
public sealed record ApiErrorResponse(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string[]>? Errors,
    string TraceId);
