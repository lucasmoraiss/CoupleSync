using System.Collections.Concurrent;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace CoupleSync.Application.AiFacts;

/// <summary>The totals of the active list, summed by the database.</summary>
public sealed record RecurringTotals(decimal Monthly, decimal Annual);

/// <summary>What changed in the transactions of a group since a calculation: how many there are and when the last one came in.</summary>
public sealed record TransactionWatermark(int Count, DateTime? LatestCreatedAtUtc);

/// <summary>One charge of a stream, as the person sees it.</summary>
public sealed record RecurringCharge(Guid TransactionId, DateTime TimestampUtc, decimal Amount, string Name);

/// <summary>
/// recurring_streams and recurring_stream_items, plus the projection of the transactions the detector reads. Every
/// method names the group.
/// </summary>
public interface IRecurringStreamRepository
{
    /// <summary>
    /// The BRL transactions of the group from <paramref name="sinceUtc"/> on, the most recent first, at most
    /// <paramref name="cap"/> rows.
    /// </summary>
    Task<IReadOnlyList<RecurrenceRow>> ReadProjectionAsync(Guid coupleId, DateTime sinceUtc, int cap, CancellationToken ct);

    Task<TransactionWatermark> GetWatermarkAsync(Guid coupleId, CancellationToken ct);

    /// <summary>When the streams of the group were last calculated, by what is stored; null when there is none.</summary>
    Task<DateTime?> GetLastDetectedAtAsync(Guid coupleId, CancellationToken ct);

    /// <summary>The streams of the group with their charges, to be changed.</summary>
    Task<List<RecurringStream>> GetForUpdateAsync(Guid coupleId, CancellationToken ct);

    Task<IReadOnlyList<RecurringStream>> ListAsync(Guid coupleId, CancellationToken ct);

    /// <summary>Monthly and annual cost of what is in the active list and is a commitment (habits and probable instalments are not).</summary>
    Task<RecurringTotals> GetTotalsAsync(Guid coupleId, CancellationToken ct);

    Task<RecurringStream?> FindAsync(Guid coupleId, Guid id, CancellationToken ct);

    Task<IReadOnlyList<RecurringCharge>> GetChargesAsync(Guid coupleId, Guid streamId, CancellationToken ct);

    Task<Dictionary<Guid, string>> GetMemberNamesAsync(Guid coupleId, CancellationToken ct);

    void Add(RecurringStream stream);

    void Remove(RecurringStream stream);

    /// <summary>Forgets what is tracked and was not stored (after a save that the database refused).</summary>
    void Reset();

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// When each group was last calculated in this process, and one calculation at a time per group. What is kept here
/// is only a shortcut: after a restart the first request calculates again, which changes nothing.
/// </summary>
public sealed class RecurrenceRunLog
{
    private readonly ConcurrentDictionary<Guid, (DateTime RanAtUtc, int TransactionCount)> _runs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public (DateTime RanAtUtc, int TransactionCount)? Get(Guid coupleId)
        => _runs.TryGetValue(coupleId, out var run) ? run : null;

    public void Set(Guid coupleId, DateTime ranAtUtc, int transactionCount)
        => _runs[coupleId] = (ranAtUtc, transactionCount);

    public SemaphoreSlim LockOf(Guid coupleId) => _locks.GetOrAdd(coupleId, _ => new SemaphoreSlim(1, 1));
}

public sealed record RecurringPerson(Guid UserId, string Name);

public sealed record RecurringInstallment(int Number, int Total, decimal RemainingAmount, string EndMonth);

public sealed record RecurringItem(
    Guid Id,
    string Name,
    string Kind,
    bool VariableAmount,
    string Cadence,
    decimal Amount,
    decimal LastAmount,
    decimal? PreviousAmount,
    decimal AnnualCost,
    int Occurrences,
    int MissedCount,
    DateOnly FirstSeen,
    DateOnly LastSeen,
    DateOnly? NextExpected,
    string Status,
    IReadOnlyList<string> Flags,
    string Confidence,
    string Category,
    RecurringPerson? Person,
    RecurringInstallment? Installment,
    string? Override);

public sealed record RecurringMonthCommitment(string Month, decimal Amount);

public sealed record RecurringList(
    decimal MonthlyTotal,
    decimal AnnualTotal,
    DateTime DetectedAtUtc,
    IReadOnlyList<RecurringItem> Subscriptions,
    IReadOnlyList<RecurringItem> FixedBills,
    IReadOnlyList<RecurringItem> Installments,
    IReadOnlyList<RecurringItem> Habits,
    IReadOnlyList<RecurringItem> Hidden,
    IReadOnlyList<RecurringMonthCommitment> InstallmentsByMonth);

/// <summary>
/// "Assinaturas e recorrências" of one group (design 3.3 and 10.3): calculates the streams when the stored ones are
/// old or a transaction came in, keeps what the person said about each one, and answers the list, the charges of a
/// stream and the correction. No AI: it works for every group.
/// </summary>
public sealed class RecurrenceService
{
    /// <summary>Tells apart two streams of the same establishment and cadence ("apple bill" and "apple bill~2").</summary>
    private const char SuffixSeparator = '~';

    private readonly IRecurringStreamRepository _repository;
    private readonly RecurrenceRunLog _runs;
    private readonly IDateTimeProvider _clock;
    private readonly RecurrenceOptions _options;

    public RecurrenceService(IRecurringStreamRepository repository, RecurrenceRunLog runs, IDateTimeProvider clock, IOptions<RecurrenceOptions> options)
    {
        _repository = repository;
        _runs = runs;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<RecurringList> GetAsync(Guid coupleId, CancellationToken ct)
    {
        var detectedAt = await RefreshIfDueAsync(coupleId, ct);

        var streams = await _repository.ListAsync(coupleId, ct);
        var totals = await _repository.GetTotalsAsync(coupleId, ct);
        var names = await _repository.GetMemberNamesAsync(coupleId, ct);
        var items = streams.Select(s => (Stream: s, Item: Map(s, names))).ToList();

        IReadOnlyList<RecurringItem> Section(string kind) => items
            .Where(i => !i.Stream.IsHidden && i.Stream.EffectiveKind == kind)
            .OrderByDescending(i => i.Stream.AnnualCost).ThenBy(i => i.Stream.DisplayName, StringComparer.Ordinal).ThenBy(i => i.Stream.Id)
            .Select(i => i.Item)
            .ToList();

        var today = Today();
        var committed = RecurrenceDetector.CommittedByMonth(
            streams
                .Where(s => !s.IsHidden && s.EffectiveKind == RecurringKinds.Installment && s.Confidence != RecurringConfidences.Low
                            && s.InstallmentNumber is not null && s.InstallmentTotal is not null)
                .Select(s => (s.LastSeenLocal, s.MedianAmount, s.InstallmentNumber!.Value, s.InstallmentTotal!.Value)),
            today);

        return new RecurringList(
            totals.Monthly,
            totals.Annual,
            detectedAt,
            Section(RecurringKinds.Subscription),
            Section(RecurringKinds.FixedBill),
            Section(RecurringKinds.Installment),
            Section(RecurringKinds.Habit),
            items.Where(i => i.Stream.IsHidden)
                .OrderBy(i => i.Stream.DisplayName, StringComparer.Ordinal).ThenBy(i => i.Stream.Id)
                .Select(i => i.Item).ToList(),
            committed.Select(c => new RecurringMonthCommitment(c.Month, c.Amount)).ToList());
    }

    public async Task<IReadOnlyList<RecurringCharge>> GetChargesAsync(Guid coupleId, Guid id, CancellationToken ct)
    {
        _ = await FindOrThrowAsync(coupleId, id, ct);
        return await _repository.GetChargesAsync(coupleId, id, ct);
    }

    /// <param name="userOverride">One of <see cref="RecurringOverrides"/>, or null to take the correction back.</param>
    public async Task<RecurringItem> SetOverrideAsync(Guid coupleId, Guid userId, Guid id, string? userOverride, CancellationToken ct)
    {
        if (userOverride is not null && !RecurringOverrides.All.Contains(userOverride))
            throw new BadRequestException("INVALID_OVERRIDE", "Correção inválida. Use NotRecurring, Cancelled, Subscription, FixedBill ou null.");

        var stream = await FindOrThrowAsync(coupleId, id, ct);
        stream.SetOverride(userOverride, userId, _clock.UtcNow);
        await _repository.SaveChangesAsync(ct);
        return Map(stream, await _repository.GetMemberNamesAsync(coupleId, ct));
    }

    private async Task<RecurringStream> FindOrThrowAsync(Guid coupleId, Guid id, CancellationToken ct)
        => await _repository.FindAsync(coupleId, id, ct)
           ?? throw new NotFoundException("RECURRENCE_NOT_FOUND", "Recorrência não encontrada.");

    // ---------------------------------------------------------------- calculation

    /// <summary>Calculates again when it is due, and answers when the streams in force were calculated.</summary>
    private async Task<DateTime> RefreshIfDueAsync(Guid coupleId, CancellationToken ct)
    {
        var gate = _runs.LockOf(coupleId);
        await gate.WaitAsync(ct);
        try
        {
            var now = _clock.UtcNow;
            var watermark = await _repository.GetWatermarkAsync(coupleId, ct);
            var remembered = _runs.Get(coupleId);
            var lastRun = remembered?.RanAtUtc ?? await _repository.GetLastDetectedAtAsync(coupleId, ct);

            var due = lastRun is null
                      || now - lastRun.Value >= TimeSpan.FromHours(_options.RefreshAfterHours)
                      || now < lastRun.Value
                      || (watermark.LatestCreatedAtUtc is { } latest && latest >= lastRun.Value)
                      || (remembered is { } r && r.TransactionCount != watermark.Count);
            if (!due) return lastRun!.Value;

            try
            {
                await RecalculateAsync(coupleId, now, ct);
            }
            catch (UniqueViolationException)
            {
                // Another instance stored the same streams at the same time: its result is the one in force.
                _repository.Reset();
            }

            _runs.Set(coupleId, now, watermark.Count);
            return now;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RecalculateAsync(Guid coupleId, DateTime nowUtc, CancellationToken ct)
    {
        var today = Today();
        var since = BrazilTime.ToUtc(today.AddMonths(-_options.HistoryMonths).ToDateTime(TimeOnly.MinValue));
        var rows = await _repository.ReadProjectionAsync(coupleId, since, _options.ProjectionRowCap, ct);
        var result = RecurrenceDetector.Detect(rows, today, _options);

        var existing = await _repository.GetForUpdateAsync(coupleId, ct);
        var unmatched = existing.ToList();
        var taken = existing.Select(e => (e.MerchantKey, e.Cadence)).ToHashSet();

        foreach (var group in result.Streams.GroupBy(d => (d.MerchantKey, d.Cadence)))
        {
            foreach (var detected in group.OrderBy(d => d.Facts.MedianAmount))
            {
                // The stored stream of this establishment and cadence that is closest: the same kind first, then the amount.
                var match = unmatched
                    .Where(e => e.Cadence == detected.Cadence && BaseKey(e.MerchantKey) == detected.MerchantKey)
                    .OrderBy(e => e.Kind == detected.Facts.Kind ? 0 : 1)
                    .ThenBy(e => Math.Abs(e.MedianAmount - detected.Facts.MedianAmount))
                    .ThenBy(e => e.MerchantKey, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (match is null)
                {
                    var key = detected.MerchantKey;
                    for (var n = 2; taken.Contains((key, detected.Cadence)); n++) key = $"{detected.MerchantKey}{SuffixSeparator}{n}";
                    taken.Add((key, detected.Cadence));
                    match = RecurringStream.Create(coupleId, key, detected.Cadence, detected.Facts, nowUtc);
                    _repository.Add(match);
                }
                else
                {
                    unmatched.Remove(match);
                    match.Apply(detected.Facts, nowUtc);
                }

                match.SetTransactions(detected.TransactionIds);
            }
        }

        foreach (var gone in unmatched)
        {
            // Only what the person said something about is kept when the detector does not find it any more.
            if (gone.UserOverride is null) _repository.Remove(gone);
            else gone.MarkNotDetected(nowUtc);
        }

        foreach (var stream in existing.Where(e => e.UserOverride == RecurringOverrides.Cancelled && e.OverrideAtUtc is not null))
        {
            var tolerance = stream.VariableAmount ? _options.VariableBillTolerance : _options.AmountTolerance;
            var chargedAgain = result.ChargesByKey.TryGetValue(BaseKey(stream.MerchantKey), out var charges)
                               && charges.Any(c => c.TimestampUtc > stream.OverrideAtUtc!.Value
                                                   && c.Amount >= stream.MedianAmount * (1 - tolerance)
                                                   && c.Amount <= stream.MedianAmount * (1 + tolerance));
            stream.SetChargedAfterCancel(chargedAgain);
        }

        await _repository.SaveChangesAsync(ct);
    }

    private static string BaseKey(string merchantKey)
    {
        var index = merchantKey.LastIndexOf(SuffixSeparator);
        return index < 0 ? merchantKey : merchantKey[..index];
    }

    private DateOnly Today() => DateOnly.FromDateTime(BrazilTime.ToLocal(_clock.UtcNow));

    private static RecurringItem Map(RecurringStream s, Dictionary<Guid, string> names) => new(
        s.Id,
        s.DisplayName,
        s.EffectiveKind,
        s.VariableAmount,
        s.Cadence,
        s.MedianAmount,
        s.LastAmount,
        s.PreviousAmount,
        s.AnnualCost,
        s.Occurrences,
        s.MissedCount,
        s.FirstSeenLocal,
        s.LastSeenLocal,
        s.NextExpectedLocal,
        s.Status,
        s.FlagList,
        s.Confidence,
        s.Category,
        s.UserId is { } userId && names.TryGetValue(userId, out var name) ? new RecurringPerson(userId, name) : null,
        s.InstallmentNumber is { } number && s.InstallmentTotal is { } total
            ? new RecurringInstallment(number, total, s.RemainingAmount ?? 0m, s.EndMonth ?? string.Empty)
            : null,
        s.UserOverride);
}
