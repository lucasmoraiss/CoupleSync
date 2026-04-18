using CoupleSync.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Infrastructure.BackgroundJobs;

public sealed class NotificationDispatcherJob : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly int MaxRetries = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFcmAdapter _fcmAdapter;
    private readonly ILogger<NotificationDispatcherJob> _logger;

    public NotificationDispatcherJob(
        IServiceScopeFactory scopeFactory,
        IFcmAdapter fcmAdapter,
        ILogger<NotificationDispatcherJob> logger)
    {
        _scopeFactory = scopeFactory;
        _fcmAdapter = fcmAdapter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NotificationDispatcherJob started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingEventsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in NotificationDispatcherJob poll loop.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("NotificationDispatcherJob stopped.");
    }

    private async Task ProcessPendingEventsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var eventRepo = scope.ServiceProvider.GetRequiredService<INotificationEventRepository>();
        var tokenRepo = scope.ServiceProvider.GetRequiredService<IDeviceTokenRepository>();
        var dateTimeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();

        var pendingEvents = await eventRepo.GetAllPendingAsync(ct);

        foreach (var notificationEvent in pendingEvents)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var deviceTokens = await tokenRepo.GetByUserIdAsync(notificationEvent.UserId, ct);

                if (deviceTokens.Count == 0)
                {
                    _logger.LogDebug(
                        "No device tokens found for UserId={UserId}. Skipping event {EventId}.",
                        notificationEvent.UserId,
                        notificationEvent.Id);
                    notificationEvent.MarkFailed();
                    await eventRepo.UpdateAsync(notificationEvent, ct);
                    await eventRepo.SaveChangesAsync(ct);
                    continue;
                }

                var anySuccess = false;

                foreach (var deviceToken in deviceTokens)
                {
                    var sent = await DispatchWithRetryAsync(
                        deviceToken.Token,
                        notificationEvent.Title,
                        notificationEvent.Body,
                        ct);

                    if (sent)
                    {
                        anySuccess = true;
                    }
                }

                if (anySuccess)
                {
                    notificationEvent.MarkDelivered(dateTimeProvider.UtcNow);
                }
                else
                {
                    notificationEvent.MarkFailed();
                }

                await eventRepo.UpdateAsync(notificationEvent, ct);
                await eventRepo.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error dispatching NotificationEvent {EventId} for UserId={UserId}.",
                    notificationEvent.Id,
                    notificationEvent.UserId);
            }
        }
    }

    private async Task<bool> DispatchWithRetryAsync(
        string token,
        string title,
        string body,
        CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var backoffSeconds = (int)Math.Pow(2, attempt - 1); // 1s, 2s
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            try
            {
                var success = await _fcmAdapter.SendAsync(token, title, body, ct);
                if (success)
                    return true;

                // false = permanent failure (invalid token etc.) — no retry
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "FCM transient error on attempt {Attempt}/{Max}.",
                    attempt + 1,
                    MaxRetries);

                if (attempt == MaxRetries - 1)
                    return false;
            }
        }

        return false;
    }
}
