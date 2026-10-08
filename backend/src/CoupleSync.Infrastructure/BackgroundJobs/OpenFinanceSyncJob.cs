using CoupleSync.Application.OpenFinance;
using CoupleSync.Infrastructure.Integrations.Pluggy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoupleSync.Infrastructure.BackgroundJobs;

/// <summary>
/// The worker of the Open Finance synchronisation queue (<c>sync_runs</c>), in the pattern of
/// <see cref="OcrBackgroundJob"/>: the queue is the table, so nothing is lost when the API sleeps and wakes up
/// (free tier). On start it fails the runs a previous process left running; then it looks at the queue every few
/// seconds and executes the waiting runs one after the other (one worker: a connection never has two runs at once).
/// </summary>
public sealed class OpenFinanceSyncJob : BackgroundService
{
    private const int MaxRunsPerPass = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OpenFinanceSyncJob> _logger;
    private readonly TimeSpan _pollInterval;

    public OpenFinanceSyncJob(
        IServiceScopeFactory scopeFactory,
        IOptions<OpenFinanceOptions> options,
        ILogger<OpenFinanceSyncJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(options.Value.SyncPollSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Never on the thread that starts the host: the API answers while the queue is looked at.
        await Task.Yield();
        _logger.LogInformation("OpenFinanceSyncJob started.");

        try
        {
            await RecoverStuckRunsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError("Could not recover Open Finance runs stuck in Running at startup ({ExceptionType}).", ex.GetType().Name);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingRunsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("Unhandled error in the OpenFinanceSyncJob poll loop ({ExceptionType}).", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OpenFinanceSyncJob stopped.");
    }

    /// <summary>Fails every run left in Running by a previous process. Public so it can be tested.</summary>
    public async Task<int> RecoverStuckRunsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var recovered = await scope.ServiceProvider.GetRequiredService<SyncConnectionService>().RecoverStuckRunsAsync(ct);
        if (recovered > 0)
            _logger.LogWarning("{Count} Open Finance run(s) stuck in Running were marked as failed.", recovered);
        return recovered;
    }

    /// <summary>One look at the queue. Public so the pass can be tested.</summary>
    public async Task<int> ProcessPendingRunsAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> pending;
        using (var scope = _scopeFactory.CreateScope())
        {
            pending = await scope.ServiceProvider.GetRequiredService<SyncConnectionService>().GetPendingRunIdsAsync(MaxRunsPerPass, ct);
        }

        foreach (var runId in pending)
        {
            ct.ThrowIfCancellationRequested();
            // Its own scope (its own unit of work): what one run leaves behind never reaches the next.
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SyncConnectionService>().ExecuteAsync(runId, ct);
        }

        return pending.Count;
    }
}
