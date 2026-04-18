namespace CoupleSync.Application.Common.Exceptions;

public class AppException : Exception
{
    public AppException(string code, string message, int statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }

    public int StatusCode { get; }
}
