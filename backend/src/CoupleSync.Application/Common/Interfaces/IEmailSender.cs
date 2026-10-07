namespace CoupleSync.Application.Common.Interfaces;

public sealed record EmailMessage(string ToAddress, string ToName, string Subject, string HtmlContent, string TextContent);

/// <summary>
/// Sends transactional e-mail. Implementations must not throw for delivery problems the caller cannot act on:
/// whether the account exists must never depend on whether the provider answered.
/// </summary>
public interface IEmailSender
{
    /// <summary>False when no provider is configured; the e-mail routes then answer 503 EMAIL_NOT_CONFIGURED.</summary>
    bool IsConfigured { get; }

    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
