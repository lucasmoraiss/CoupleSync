namespace CoupleSync.Application.Common.Exceptions;

public sealed class ForbiddenException : AppException
{
    public ForbiddenException(string code, string message)
        : base(code, message, 403)
    {
    }
}
