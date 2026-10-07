using System.Threading.Channels;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.Integrations.Email;

/// <summary>
/// Production <see cref="IEmailSender"/>: puts the message on a queue and returns at once, so a request never waits for
/// the provider (the largest timing difference between a known and an unknown address) and a slow or failing provider
/// never delays or breaks a request. It does not make response times identical.
/// When no provider is configured <see cref="IsConfigured"/> is false and nothing is queued.
/// </summary>
public sealed class QueuedEmailSender : IEmailSender
{
    private readonly Channel<EmailMessage> _channel;
    private readonly EmailOptions _options;
    private readonly ILogger<QueuedEmailSender> _logger;

    public QueuedEmailSender(IOptions<EmailOptions> options, ILogger<QueuedEmailSender> logger, int capacity = 200)
    {
        _options = options.Value;
        _logger = logger;
        _channel = Channel.CreateBounded<EmailMessage>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true },
            // With DropWrite, TryWrite still returns true when it drops: only this callback sees the loss.
            _ => _logger.LogWarning("E-mail queue is full; a message was dropped."));
    }

    public bool IsConfigured => _options.IsConfigured;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (IsConfigured)
        {
            _channel.Writer.TryWrite(message);
        }

        return Task.CompletedTask;
    }

    internal ChannelReader<EmailMessage> Reader => _channel.Reader;
}

/// <summary>Drains <see cref="QueuedEmailSender"/> through the Brevo client. A failed delivery is logged (status only) and dropped.</summary>
public sealed class EmailDispatchService : BackgroundService
{
    private readonly QueuedEmailSender _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EmailDispatchService> _logger;

    public EmailDispatchService(QueuedEmailSender queue, IServiceScopeFactory scopeFactory, ILogger<EmailDispatchService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<BrevoEmailClient>().SendAsync(message, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The message carries a one-time code and the recipient: log the failure reason only.
                    _logger.LogWarning("E-mail delivery failed: {Reason}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
