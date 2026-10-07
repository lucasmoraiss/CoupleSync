namespace CoupleSync.Api.Errors;

/// <summary>Codes produced by the framework layer (not by a specific business rule).</summary>
public static class ApiErrorCodes
{
    public const string ValidationError = "VALIDATION_ERROR";
    public const string InvalidRequestBody = "INVALID_REQUEST_BODY";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string CoupleRequired = "COUPLE_REQUIRED";
    public const string NotFound = "NOT_FOUND";
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string BadRequest = "BAD_REQUEST";
    public const string InternalServerError = "INTERNAL_SERVER_ERROR";
}
