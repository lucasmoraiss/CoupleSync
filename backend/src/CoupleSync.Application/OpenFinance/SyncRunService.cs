using System.Globalization;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.OpenFinance;

/// <summary>
/// The queue of synchronisations: who connected asks for one (the job executes it), anyone of the group follows it,
/// and the daily scheduler enqueues one for each connection that went a day without a successful one.
/// </summary>
public sealed class SyncRunService
{
    public const string TooSoonCode = "SYNC_TOO_SOON";
    public const string AlreadyRunningCode = "SYNC_ALREADY_RUNNING";
    public const string RunNotFoundCode = "SYNC_RUN_NOT_FOUND";

    public const string AlreadyRunningMessage =
        "Já há uma sincronização em andamento para esta conexão. Aguarde ela terminar.";

    /// <summary>One run per connection in this interval, whoever asked and however it ended.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    /// <summary>The hour (Brasília) from which the scheduler enqueues the runs of the day.</summary>
    public const int DailyHour = 6;

    /// <summary>The scheduler leaves alone a connection whose last successful synchronisation is younger than this.</summary>
    public static readonly TimeSpan SchedulerMinAge = TimeSpan.FromHours(20);

    private readonly IBankSyncRepository _sync;
    private readonly IBankConnectionRepository _connections;
    private readonly ICoupleMembership _membership;
    private readonly ICredentialCipher _cipher;
    private readonly IDateTimeProvider _clock;

    public SyncRunService(
        IBankSyncRepository sync,
        IBankConnectionRepository connections,
        ICoupleMembership membership,
        ICredentialCipher cipher,
        IDateTimeProvider clock)
    {
        _sync = sync;
        _connections = connections;
        _membership = membership;
        _cipher = cipher;
        _clock = clock;
    }

    /// <summary>
    /// Enqueues a run for the caller's own connection. <paramref name="force"/> (ask Pluggy to read the banks again
    /// first) only counts when the person asked, never for the silent request of the app being opened.
    /// <paramref name="historyMonths"/>, when given, becomes the period of the connection (the choice of the last
    /// step of the wizard) in the same write that enqueues the run.
    /// </summary>
    public async Task<SyncRunDto> RequestAsync(
        Guid coupleId, Guid userId, Guid connectionId, bool force, bool appOpen, bool aiCategorizationConsent, int? historyMonths, CancellationToken ct)
    {
        if (historyMonths is { } months && !BankConnection.AllowedHistoryMonths.Contains(months))
        {
            const string message = "O período deve ser de 3, 6 ou 12 meses.";
            throw new BadRequestException("VALIDATION_ERROR", message, new Dictionary<string, string[]> { ["historyMonths"] = [message] });
        }

        if (!_cipher.IsAvailable)
            throw new AppException(OpenFinanceService.UnavailableCode, OpenFinanceService.UnavailableMessage, 503);

        var connection = await _connections.FindConnectionAsync(connectionId, coupleId, ct)
            ?? throw new NotFoundException("BANK_CONNECTION_NOT_FOUND", "Conexão bancária não encontrada.");

        if (connection.UserId != userId)
            throw new ForbiddenException("BANK_CONNECTION_FORBIDDEN", "Só quem conectou pode sincronizar esta conexão bancária.");

        if (connection.Status == BankConnectionStatus.Disconnected || !connection.HasCredentials)
            throw new ConflictException(OpenFinanceService.DisconnectedCode, SyncConnectionService.DisconnectedMessage);

        var now = _clock.UtcNow;
        var latest = await _sync.GetLatestRunAsync(connection.Id, coupleId, ct);
        // One still waiting or running (whenever it was asked for): there is no hour to promise, only to wait for it.
        if (latest is { IsOpen: true })
            throw AlreadyRunning();
        if (latest is not null && now - latest.CreatedAtUtc < MinInterval)
            throw TooSoon(latest.CreatedAtUtc + MinInterval);

        if (historyMonths is { } chosen)
            connection.SetHistoryMonths(chosen, now);

        var run = SyncRun.Create(
            coupleId,
            connection.Id,
            appOpen ? SyncRunTrigger.AppOpen : SyncRunTrigger.User,
            forceItemUpdate: force && !appOpen,
            aiCategorizationConsent,
            now);
        await _sync.AddRunAsync(run, ct);
        try
        {
            await _sync.SaveChangesAsync(ct);
        }
        catch (UniqueViolationException)
        {
            // Another request (or the scheduler) enqueued one at this very moment: the database keeps a single
            // run waiting or running per connection.
            throw AlreadyRunning();
        }
        catch (ForeignKeyViolationException)
        {
            // The connection was deleted meanwhile: the person left the group.
            throw new NotFoundException("BANK_CONNECTION_NOT_FOUND", "Conexão bancária não encontrada.");
        }
        catch (ConcurrencyConflictException)
        {
            // The period was being written to a connection that was disconnected (or connected again) meanwhile.
            throw new ConflictException(OpenFinanceService.ChangedCode, "Esta conexão mudou enquanto a sincronização era pedida. Tente de novo.");
        }

        return Map(run);
    }

    /// <summary>Anyone of the group follows a run of the group.</summary>
    public async Task<SyncRunDto> GetAsync(Guid coupleId, Guid runId, CancellationToken ct)
    {
        var run = await _sync.FindRunAsync(runId, coupleId, ct)
            ?? throw new NotFoundException(RunNotFoundCode, "Sincronização não encontrada.");
        return Map(run);
    }

    /// <summary>
    /// The daily pass, safe to repeat: from 06:00 of Brasília on, one run for each connection that can be
    /// synchronised, belongs to someone still in the group, was synchronised by its owner at least once, had no
    /// successful synchronisation in the last 20 hours and has no run waiting, running or created in those hours.
    /// Returns how many were enqueued.
    /// </summary>
    public async Task<int> EnqueueDailyRunsAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (BrazilTime.ToLocal(now).Hour < DailyHour || !_cipher.IsAvailable) return 0;

        var threshold = now - SchedulerMinAge;
        var enqueued = 0;
        foreach (var connection in await _sync.GetConnectionsToScheduleAsync(ct))
        {
            if (!connection.HasCredentials) continue;
            // Never synchronised: the person has not chosen the period yet. The first one is always asked by them.
            if (connection.LastSyncAtUtc is not { } last || last > threshold) continue;
            if (await _sync.HasOpenOrRecentRunAsync(connection.Id, threshold, ct)) continue;
            if (!await _membership.IsMemberAsync(connection.UserId, connection.CoupleId, ct)) continue;

            var run = SyncRun.Create(
                connection.CoupleId, connection.Id, SyncRunTrigger.Scheduler, forceItemUpdate: false, aiCategorizationConsent: false, now);
            await _sync.AddRunAsync(run, ct);
            try
            {
                await _sync.SaveChangesAsync(ct);
                enqueued++;
            }
            catch (DataStoreException ex) when (ex is UniqueViolationException or ForeignKeyViolationException)
            {
                // Someone asked for a run at this very moment, or the connection is gone: nothing to enqueue.
                _sync.DiscardChanges();
            }
        }

        return enqueued;
    }

    private static AppException AlreadyRunning() => new ConflictException(AlreadyRunningCode, AlreadyRunningMessage);

    private static AppException TooSoon(DateTime nextAtUtc)
    {
        var next = DateTime.SpecifyKind(nextAtUtc, DateTimeKind.Utc);
        var local = BrazilTime.ToLocal(next).ToString("HH:mm", CultureInfo.InvariantCulture);
        return new AppException(
            TooSoonCode,
            $"Esta conexão foi sincronizada há pouco. A próxima sincronização pode ser pedida às {local} (horário de Brasília).",
            409,
            new Dictionary<string, string[]>
            {
                ["nextSyncAtUtc"] = [next.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)],
            });
    }

    private static SyncRunDto Map(SyncRun run) => new(
        run.Id,
        run.ConnectionId,
        run.Status.ToString(),
        run.TriggeredBy.ToString(),
        run.CreatedAtUtc,
        run.StartedAtUtc,
        run.FinishedAtUtc,
        run.TransactionsNew,
        run.TransactionsUpdated,
        run.ErrorCode,
        run.ErrorMessage);
}
