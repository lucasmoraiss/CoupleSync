using System.Net.Mail;

namespace CoupleSync.Domain.ValueObjects;

public readonly record struct EmailAddress
{
    private EmailAddress(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static EmailAddress From(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("O e-mail é obrigatório.", nameof(value));
        }

        var normalized = value.Trim().ToLowerInvariant();

        try
        {
            _ = new MailAddress(normalized);
        }
        catch (FormatException)
        {
            throw new ArgumentException("O formato do e-mail é inválido.", nameof(value));
        }

        return new EmailAddress(normalized);
    }
}
