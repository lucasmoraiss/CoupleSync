using CoupleSync.Application.Auth;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Support;

/// <summary>In-memory sender: nothing leaves the process. Keeps every message and can be told to fail.</summary>
public sealed class InMemoryEmailSender : IEmailSender
{
    public bool IsConfigured { get; set; } = true;

    public Exception? Failure { get; set; }

    public List<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }

    /// <summary>The six-digit code in the plain-text body of a sent message.</summary>
    public static string CodeOf(EmailMessage message) =>
        System.Text.RegularExpressions.Regex.Match(message.TextContent, @"\b\d{6}\b").Value;
}

public sealed class MutableDateTimeProvider : IDateTimeProvider
{
    public MutableDateTimeProvider(DateTime utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTime UtcNow { get; set; }
}

public static class EmailTestKit
{
    public static VerificationCodeService NewCodeService() =>
        new(Options.Create(new JwtOptions { Secret = "this-is-a-secure-test-secret-with-32chars" }));

    public static EmailCodeFlow NewFlow(FakeAuthRepository repository, InMemoryEmailSender sender, IDateTimeProvider clock) =>
        new(repository, NewCodeService(), sender, clock, NullLogger<EmailCodeFlow>.Instance);
}
