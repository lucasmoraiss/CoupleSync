using CoupleSync.Application.OpenFinance;
using CoupleSync.Infrastructure.Integrations.Pluggy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.BackgroundJobs;

/// <summary>
/// Once a day, from 06:00 of Brasília on, enqueues a synchronisation for each connection that went 20 hours without
/// a successful one. It keeps nothing in memory and waits for no distant hour: every tick asks the store what is
/// missing (<see cref="SyncRunService.EnqueueDailyRunsAsync"/>), so it works whenever the API happens to be awake
/// and repeating it enqueues nothing twice. With the API asleep nothing runs: the app asks when it is opened.
/// </summary>
public sealed class OpenFinanceDailyScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OpenFinanceDailyScheduler> _logger;
    private readonly double _tickSeconds;

    public OpenFinanceDailyScheduler(
        IServiceScopeFactory scopeFactory,
        IOptions<OpenFinanceOptions> options,
        ILogger<OpenFinanceDailyScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _tickSeconds = options.Value.SchedulerTickSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (_tickSeconds <= 0)
        {
            _logger.LogInformation("OpenFinanceDailyScheduler is turned off by configuration.");
            return;
        }

        var tick = TimeSpan.FromSeconds(_tickSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnqueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("Unhandled error in the OpenFinanceDailyScheduler ({ExceptionType}).", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One tick. Public so it can be tested.</summary>
    public async Task<int> EnqueueAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var enqueued = await scope.ServiceProvider.GetRequiredService<SyncRunService>().EnqueueDailyRunsAsync(ct);
        if (enqueued > 0)
            _logger.LogInformation("{Count} Open Finance run(s) enqueued by the daily scheduler.", enqueued);
        return enqueued;
    }
}
