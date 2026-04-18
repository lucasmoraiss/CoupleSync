namespace CoupleSync.Application.Common.Exceptions;

public sealed class OcrException : AppException
{
    public OcrException(string code, string message)
        : base(code, message, 422)
    {
    }
}
