namespace CoupleSync.Application.Common.Exceptions;

/// <summary>
/// Business error that is answered with the single API error format
/// (<c>{ code, message, errors?, traceId }</c>). <see cref="Message"/> is shown to the user, so it
/// must be written in Brazilian Portuguese. Raise one of the derived exceptions (or this class with an
/// explicit status) from any handler/controller; <c>GlobalExceptionMiddleware</c> does the rest.
/// </summary>
public class AppException : Exception
{
    public AppException(string code, string message, int statusCode)
        : this(code, message, statusCode, errors: null)
    {
    }

    public AppException(string code, string message, int statusCode, IReadOnlyDictionary<string, string[]>? errors)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Errors = errors;
    }

    public string Code { get; }

    public int StatusCode { get; }

    /// <summary>Field name to list of messages; only filled for validation errors.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; }
}
