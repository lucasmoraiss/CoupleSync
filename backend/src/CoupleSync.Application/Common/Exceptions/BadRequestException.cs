namespace CoupleSync.Application.Common.Exceptions;

public sealed class BadRequestException : AppException
{
    public BadRequestException(string code, string message)
        : base(code, message, 400)
    {
    }

    public BadRequestException(string code, string message, IReadOnlyDictionary<string, string[]> errors)
        : base(code, message, 400, errors)
    {
    }
}
