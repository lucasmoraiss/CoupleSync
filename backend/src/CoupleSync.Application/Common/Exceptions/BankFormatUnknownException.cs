namespace CoupleSync.Application.Common.Exceptions;

public sealed class BankFormatUnknownException : AppException
{
    public BankFormatUnknownException()
        : base("BANK_FORMAT_UNKNOWN", "Formato de extrato bancário não reconhecido.", 422)
    {
    }
}
