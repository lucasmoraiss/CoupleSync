namespace CoupleSync.Application.Common.Exceptions;

public sealed class UnprocessableEntityException : AppException
{
    public UnprocessableEntityException(string code, string message)
        : base(code, message, 422)
    {
    }
}
