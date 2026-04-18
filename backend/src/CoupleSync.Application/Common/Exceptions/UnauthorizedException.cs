namespace CoupleSync.Application.Common.Exceptions;

public sealed class UnauthorizedException : AppException
{
    public UnauthorizedException(string code, string message)
        : base(code, message, 401)
    {
    }
}
