using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoupleSync.Infrastructure.Integrations.Email;

public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// E-mail (Brevo over HTTPS). Without Email__* configured <see cref="IEmailSender.IsConfigured"/> is false and the
    /// e-mail routes answer 503. Sending goes through an in-process queue drained by <see cref="EmailDispatchService"/>.
    /// </summary>
    public static IServiceCollection AddEmailSending(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.AddHttpClient<BrevoEmailClient>(c => c.Timeout = TimeSpan.FromSeconds(10))
            // HttpClient logging prints request headers at Trace; the key must never reach a log.
            .RedactLoggedHeaders(header => string.Equals(header, "api-key", StringComparison.OrdinalIgnoreCase));
        services.AddSingleton<QueuedEmailSender>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<QueuedEmailSender>());
        services.AddHostedService<EmailDispatchService>();
        return services;
    }
}
