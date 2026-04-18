namespace CoupleSync.Application.Common.Exceptions;

public sealed class ConflictException : AppException
{
    public ConflictException(string code, string message)
        : base(code, message, 409)
    {
    }
}
