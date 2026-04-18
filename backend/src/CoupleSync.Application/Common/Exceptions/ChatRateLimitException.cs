namespace CoupleSync.Application.Common.Exceptions;

public sealed class ChatRateLimitException : AppException
{
    public ChatRateLimitException(string code, string message)
        : base(code, message, 429)
    {
    }
}
