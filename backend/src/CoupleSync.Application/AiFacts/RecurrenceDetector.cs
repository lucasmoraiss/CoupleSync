using CoupleSync.Application.Ai;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.AiFacts;

/// <summary>
/// The numbers of the recurrence engine (design 3.3, 3.4 and 3.2). The defaults are the ones of the design; the
/// "Recurrence" configuration section replaces any of them.
/// </summary>
public sealed class RecurrenceOptions
{
    public const string SectionName = "Recurrence";

    /// <summary>How far back the detector reads.</summary>
    public int HistoryMonths { get; set; } = 13;

    /// <summary>Ceiling of rows read for one group (the most recent ones).</summary>
    public int ProjectionRowCap { get; set; } = 20_000;

    /// <summary>A calculation older than this is done again when the list is asked for.</summary>
    public int RefreshAfterHours { get; set; } = 6;

    public int WeeklyMinOccurrences { get; set; } = 4;

    public int MonthlyMinOccurrences { get; set; } = 3;

    public int YearlyMinOccurrences { get; set; } = 2;

    /// <summary>Band of the amount of a subscription or purchase, around the median.</summary>
    public decimal AmountTolerance { get; set; } = 0.15m;

    /// <summary>Band of a "variable fixed bill" (electricity, water...), around the median.</summary>
    public decimal VariableBillTolerance { get; set; } = 0.40m;

    /// <summary>Two charges are "the same price" within this fraction. Above it, it is a change of price.</summary>
    public decimal SamePriceTolerance { get; set; } = 0.05m;

    /// <summary>A chain has to hold at least this share of the similar charges of its period (otherwise it was picked out of a denser habit).</summary>
    public decimal MinChainCoverage { get; set; } = 0.6m;

    /// <summary>
    /// A stream is "new" while its first known charge is at most this old (Brasília days). A monthly series only
    /// exists with its third charge, about 60 days after the first: the mark is shown from then until this limit.
    /// </summary>
    public int NewWithinDays { get; set; } = 90;

    /// <summary>A single instalment mark is only shown, as probable, while it is this fresh.</summary>
    public int ProbableInstallmentWithinDays { get; set; } = 35;

    /// <summary>"Got more expensive" is shown while the new price has at most this many charges.</summary>
    public int PriceIncreaseMaxCharges { get; set; } = 3;

    /// <summary>
    /// How many series of the same amount one establishment may have at the same time (the same plan paid by each
    /// person of the couple). More than that is read as one habit, not as several commitments.
    /// </summary>
    public int MaxSeriesPerAmount { get; set; } = 2;

    /// <summary>How many days a charge may be away from its day of the month for the series to count as "always on the same day".</summary>
    public int SameDayToleranceDays { get; set; } = 3;

    public decimal DormantAfterIntervals { get; set; } = 1.5m;

    public decimal StoppedAfterIntervals { get; set; } = 2m;

    /// <summary>Above this median a LAZER/COMPRAS/OUTROS charge is not called a subscription.</summary>
    public decimal SubscriptionMaxAmount { get; set; } = 200m;

    public int ForgottenAfterMonths { get; set; } = 6;

    public decimal ForgottenMaxAmount { get; set; } = 60m;

    public int InstallmentMaxParts { get; set; } = 48;

    public int HabitWindowDays { get; set; } = 30;

    public int HabitMinOccurrences { get; set; } = 4;

    public decimal HabitMaxMedian { get; set; } = 50m;
}

/// <summary>One transaction as the detector reads it. The description never leaves the server.</summary>
public sealed record RecurrenceRow(
    Guid Id,
    DateTime TimestampUtc,
    decimal Amount,
    string? Merchant,
    string? Description,
    string Category,
    Guid UserId,
    TransactionSource Source)
{
    public DateOnly LocalDate { get; } = DateOnly.FromDateTime(BrazilTime.ToLocal(TimestampUtc));

    /// <summary>What the app shows as the title of the transaction: the merchant, or the description when there is none.</summary>
    public string? Establishment => string.IsNullOrWhiteSpace(Merchant) ? (string.IsNullOrWhiteSpace(Description) ? null : Description.Trim()) : Merchant.Trim();
}

/// <summary>One stream found: its key (without the suffix that tells two streams of the same shop apart), its facts and its charges.</summary>
public sealed record DetectedStream(string MerchantKey, string Cadence, RecurringStreamFacts Facts, IReadOnlyList<Guid> TransactionIds);

/// <summary>One charge of an establishment, for "was it charged again after the person cancelled?" and "is the last charge of a yearly stream still there?".</summary>
public sealed record KeyCharge(Guid TransactionId, DateTime TimestampUtc, decimal Amount)
{
    public DateOnly LocalDate { get; } = DateOnly.FromDateTime(BrazilTime.ToLocal(TimestampUtc));
}

public sealed record DetectionResult(IReadOnlyList<DetectedStream> Streams, IReadOnlyDictionary<string, IReadOnlyList<KeyCharge>> ChargesByKey);

/// <summary>
/// Finds what repeats in the transactions of one group — subscriptions, fixed bills (also the ones whose amount
/// varies), purchases in instalments and habits — with code only (design 3.3, 3.4 and 3.2). Pure: the same rows and
/// the same "today" always give the same answer.
/// </summary>
public static class RecurrenceDetector
{
    /// <summary>The longest interval between two charges of a yearly stream (design 3.3, item 3).</summary>
    public const int YearlyMaxIntervalDays = 395;

    /// <param name="FromDescription">The transaction has no merchant: the text is its description, cleaned.</param>
    private sealed record Keyed(RecurrenceRow Row, string Key, string Text, bool FromDescription)
    {
        public DateOnly Date => Row.LocalDate;

        public decimal Amount => Row.Amount;
    }

    private sealed record CadenceSpec(string Name, int Min, int Max, int? MissedMin, int? MissedMax, int NominalDays, int MinOccurrences, decimal PerYear);

    private sealed record Chain(CadenceSpec Cadence, List<Keyed> Rows, int Missed, bool Variable);

    public static DetectionResult Detect(IReadOnlyList<RecurrenceRow> rows, DateOnly today, RecurrenceOptions options)
    {
        var keyed = new List<Keyed>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Amount <= 0 || row.LocalDate > today) continue;
            var text = row.Establishment;
            if (text is null) continue;
            if (PersonTransferRule.IsPersonTransfer(row.Merchant, row.Description, text)) continue;

            // A name read from a description (a statement line, a note typed by the person) is free text: with a
            // document, a phone, an e-mail or a Pix key in it the charge forms no stream at all; any other long
            // number only leaves the name.
            var fromDescription = string.IsNullOrWhiteSpace(row.Merchant);
            if (fromDescription)
            {
                if (FactPackPrivacyFilter.HasDocumentOrContact(text)) continue;
                text = FactPackPrivacyFilter.RemoveLongNumbers(text);
            }

            var key = MerchantKey.Normalize(text);
            if (key.Length == 0 || key == MerchantKey.PersonTransfer) continue;
            keyed.Add(new Keyed(row, key, text, fromDescription));
        }

        var streams = new List<DetectedStream>();
        var consumed = new HashSet<Guid>();
        DetectInstallments(keyed, today, options, streams, consumed);

        foreach (var group in keyed.GroupBy(k => k.Key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var all = group.OrderBy(k => k.Date).ThenBy(k => k.Row.TimestampUtc).ThenBy(k => k.Row.Id).ToList();
            var pool = all.Where(k => !consumed.Contains(k.Row.Id)).ToList();
            if (pool.Count == 0) continue;

            var found = DetectCadences(group.Key, pool, today, options);
            streams.AddRange(found);
            if (found.Count == 0 && DetectFrequentSmallSpend(group.Key, pool, today, options) is { } habit) streams.Add(habit);
        }

        var charges = keyed
            .GroupBy(k => k.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<KeyCharge>)g.Select(k => new KeyCharge(k.Row.Id, k.Row.TimestampUtc, k.Amount)).ToList(),
                StringComparer.Ordinal);
        return new DetectionResult(streams, charges);
    }

    /// <summary>
    /// What the confirmed instalments still commit, month by month ("yyyy-MM"), for the 12 months starting at the
    /// month of <paramref name="today"/>. A month whose instalment was already charged is not counted again.
    /// </summary>
    public static IReadOnlyList<(string Month, decimal Amount)> CommittedByMonth(
        IEnumerable<(DateOnly LastSeen, decimal Amount, int Number, int Total)> installments, DateOnly today)
    {
        var first = MonthIndex(today);
        var totals = new decimal[12];
        foreach (var (lastSeen, amount, number, total) in installments)
        {
            var last = MonthIndex(lastSeen);
            for (var part = 1; part <= total - number; part++)
            {
                var offset = last + part - first;
                if (offset is >= 0 and < 12) totals[offset] += amount;
            }
        }

        return Enumerable.Range(0, 12).Select(i => (MonthText(first + i), totals[i])).ToList();
    }

    // ---------------------------------------------------------------- instalments (3.4)

    private static void DetectInstallments(List<Keyed> keyed, DateOnly today, RecurrenceOptions options, List<DetectedStream> streams, HashSet<Guid> consumed)
    {
        var marked = new List<(Keyed Row, int Number, int Total)>();
        foreach (var k in keyed)
        {
            var mark = FindMark(k.Row.Merchant, options) ?? FindMark(k.Row.Description, options);
            if (mark is { } m) marked.Add((k, m.Number, m.Total));
        }

        // The same purchase: same shop, same number of parts, and each part in the month its number says.
        var plans = marked
            .GroupBy(m => (m.Row.Key, m.Total, Start: MonthIndex(m.Row.Date) - (m.Number - 1)))
            .OrderBy(g => g.Key.Key, StringComparer.Ordinal).ThenBy(g => g.Key.Start).ThenBy(g => g.Key.Total);

        foreach (var plan in plans)
        {
            var parts = plan.OrderBy(p => p.Number).ThenBy(p => p.Row.Date).ToList();
            var numbers = parts.Select(p => p.Number).Distinct().ToList();
            var confirmed = numbers.Any(n => numbers.Contains(n + 1));
            var last = parts[^1];
            var endMonth = plan.Key.Start + plan.Key.Total - 1;
            var daysSince = today.DayNumber - last.Row.Date.DayNumber;

            if (!confirmed)
            {
                // One mark alone may be a date ("15/10"). It is only shown, as probable, while it is fresh and does
                // not look like the day and month of the purchase itself.
                var looksLikeDate = last.Total == last.Row.Date.Month;
                if (looksLikeDate || daysSince > options.ProbableInstallmentWithinDays || last.Number == last.Total) continue;
            }

            if (MonthIndex(today) > endMonth) continue;   // paid off

            // Two purchases of the same plan in the same shop and month count as one commitment.
            var amount = parts.Where(p => p.Number == last.Number).Sum(p => p.Row.Amount);
            var remainingParts = plan.Key.Total - last.Number;
            var status = remainingParts == 0 ? RecurringStatuses.Active : Lifecycle(daysSince, 30m, options);
            var users = parts.Select(p => p.Row.Row.UserId).Distinct().ToList();

            var facts = new RecurringStreamFacts(
                DisplayName: DisplayName(MerchantKey.RemoveInstallmentMark(last.Row.Text)),
                NameSource: NameSource(parts.Select(p => p.Row)),
                Kind: RecurringKinds.Installment,
                VariableAmount: false,
                Category: ModeCategory(parts.Select(p => p.Row)),
                UserId: users.Count == 1 ? users[0] : null,
                MedianAmount: amount,
                LastAmount: amount,
                PreviousAmount: null,
                AnnualCost: amount * Math.Min(remainingParts, 12),
                Occurrences: numbers.Count,
                MissedCount: 0,
                FirstSeenLocal: parts.Min(p => p.Row.Date),
                LastSeenLocal: last.Row.Date,
                NextExpectedLocal: remainingParts > 0 ? last.Row.Date.AddMonths(1) : null,
                Status: status,
                Flags: [],
                Confidence: confirmed ? RecurringConfidences.High : RecurringConfidences.Low,
                InstallmentNumber: last.Number,
                InstallmentTotal: plan.Key.Total,
                RemainingAmount: amount * remainingParts,
                EndMonth: MonthText(endMonth));

            streams.Add(new DetectedStream(
                $"{plan.Key.Key}#{plan.Key.Total}x{MonthText(plan.Key.Start).Replace("-", string.Empty, StringComparison.Ordinal)}",
                RecurringCadences.Monthly,
                facts,
                parts.Select(p => p.Row.Row.Id).ToList()));

            // Only a confirmed plan takes its charges out of the other detections.
            if (confirmed)
            {
                foreach (var p in parts) consumed.Add(p.Row.Row.Id);
            }
        }
    }

    private static (int Number, int Total)? FindMark(string? text, RecurrenceOptions options)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (System.Text.RegularExpressions.Match match in MerchantKey.InstallmentMark().Matches(text))
        {
            // "05/10/2026" is a date, not part 5 of 10.
            var before = match.Index > 0 ? text[match.Index - 1] : ' ';
            var after = match.Index + match.Length < text.Length ? text[match.Index + match.Length] : ' ';
            if (before is '/' or '-' or '.' || after is '/' or '-') continue;

            var number = int.Parse(match.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
            var total = int.Parse(match.Groups[2].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
            if (number >= 1 && number <= total && total >= 2 && total <= options.InstallmentMaxParts) return (number, total);
        }

        return null;
    }

    // ---------------------------------------------------------------- cadences (3.3)

    private static List<DetectedStream> DetectCadences(string key, List<Keyed> pool, DateOnly today, RecurrenceOptions o)
    {
        var monthly = new CadenceSpec(RecurringCadences.Monthly, 25, 34, 53, 65, 30, o.MonthlyMinOccurrences, 12m);
        var weekly = new CadenceSpec(RecurringCadences.Weekly, 5, 9, 12, 16, 7, o.WeeklyMinOccurrences, 52m);
        var yearly = new CadenceSpec(RecurringCadences.Yearly, 335, 395, null, null, 365, o.YearlyMinOccurrences, 1m);

        var category = ModeCategory(pool);
        var utility = MerchantHints.IsUtility(key);
        var variableEligible = category is "MORADIA" or "SAUDE" || utility;
        var chosen = new List<Chain>();
        var remaining = pool.ToList();

        // Track "variable fixed bill" first where it applies: its band holds every charge the narrow band would.
        if (variableEligible)
        {
            foreach (var cluster in Cluster(remaining, o.VariableBillTolerance))
            {
                foreach (var chain in SeriesOf(cluster, monthly, o))
                {
                    var median = Median(chain.Select(r => r.Amount));
                    var variable = chain.Any(r => !Within(r.Amount, median, o.AmountTolerance));
                    chosen.Add(new Chain(monthly, chain, CountMissed(chain, monthly), variable));
                }
            }

            RemoveUsed(remaining, chosen);
        }

        // Track "subscription and purchase": every cluster of similar amounts, every cadence, plus one price step.
        var candidates = new List<Chain>();
        var clusters = Cluster(remaining, o.AmountTolerance);
        foreach (var cadence in new[] { monthly, weekly, yearly })
        {
            var raw = new List<List<Keyed>>();
            foreach (var cluster in clusters)
            {
                var chain = RawChain(cluster, cadence);
                if (chain.Count == 0) continue;
                raw.Add(chain);
                foreach (var series in SeriesOf(cluster, cadence, o))
                    candidates.Add(new Chain(cadence, series, CountMissed(series, cadence), false));
            }

            // One step of price: the old price in one cluster, the new one in another, the dates in the same rhythm.
            foreach (var newer in raw.Where(c => c.Count >= 2))
            {
                var older = raw
                    .Where(c => c.Count >= 2 && c[^1].Date < newer[0].Date && Link(cadence, newer[0].Date.DayNumber - c[^1].Date.DayNumber) != LinkKind.None)
                    .OrderByDescending(c => c[^1].Date)
                    .FirstOrDefault();
                if (older is null) continue;
                var merged = older.Concat(newer).ToList();
                candidates.Add(new Chain(cadence, merged, CountMissed(merged, cadence), false));
            }
        }

        var used = new HashSet<Guid>();
        foreach (var candidate in candidates
                     .Where(c => c.Rows.Count >= c.Cadence.MinOccurrences)
                     .OrderByDescending(c => c.Rows.Count)
                     .ThenBy(c => c.Cadence.NominalDays == 30 ? 0 : c.Cadence.NominalDays)
                     .ThenByDescending(c => c.Rows[^1].Date))
        {
            if (candidate.Rows.Any(r => used.Contains(r.Row.Id))) continue;
            foreach (var r in candidate.Rows) used.Add(r.Row.Id);
            chosen.Add(candidate);
        }

        return chosen
            .Where(c => c.Rows.Count >= c.Cadence.MinOccurrences)
            .OrderBy(c => c.Rows[0].Date).ThenBy(c => c.Rows[0].Amount).ThenBy(c => c.Rows[0].Row.Id)
            .Select(c => Build(key, c, utility, today, o))
            .ToList();
    }

    private static void RemoveUsed(List<Keyed> remaining, List<Chain> chosen)
    {
        var ids = chosen.SelectMany(c => c.Rows).Select(r => r.Row.Id).ToHashSet();
        remaining.RemoveAll(r => ids.Contains(r.Row.Id));
    }

    private static DetectedStream Build(string key, Chain chain, bool utility, DateOnly today, RecurrenceOptions o)
    {
        var rows = chain.Rows;
        var cadence = chain.Cadence;
        var last = rows[^1];

        // The price of today: the last charges that cost the same. What came before, at another price, is the previous one.
        var currentFrom = rows.Count - 1;
        while (currentFrom > 0 && Within(rows[currentFrom - 1].Amount, last.Amount, o.SamePriceTolerance)) currentFrom--;
        var current = rows.Skip(currentFrom).ToList();
        decimal amount;
        decimal? previous = null;
        var priceIncrease = false;
        DateOnly priceSince = rows[0].Date;

        if (chain.Variable)
        {
            amount = Median(rows.TakeLast(3).Select(r => r.Amount));
        }
        else if (currentFrom == 0)
        {
            amount = Median(rows.Select(r => r.Amount));
        }
        else
        {
            var before = rows.Take(currentFrom).ToList();
            var oldFrom = before.Count - 1;
            while (oldFrom > 0 && Within(before[oldFrom - 1].Amount, before[^1].Amount, o.SamePriceTolerance)) oldFrom--;
            var old = before.Skip(oldFrom).ToList();
            var oldPrice = Median(old.Select(r => r.Amount));
            var newPrice = Median(current.Select(r => r.Amount));

            if (current.Count >= 2 && old.Count >= 2)
            {
                // A step followed by a stable price: the same item, at a new price.
                amount = newPrice;
                priceSince = current[0].Date;
                if (newPrice > oldPrice)
                {
                    previous = oldPrice;
                    // The price before stays as a fact; the mark is news, and goes away after a few charges.
                    priceIncrease = current.Count <= o.PriceIncreaseMaxCharges;
                }
            }
            else
            {
                // A single different charge is not a new price yet.
                amount = current.Count >= 2 ? newPrice : Median(rows.Select(r => r.Amount));
                priceSince = current.Count >= 2 ? current[0].Date : last.Date;
            }
        }

        var intervals = new List<int>();
        for (var i = 1; i < rows.Count; i++)
        {
            var gap = rows[i].Date.DayNumber - rows[i - 1].Date.DayNumber;
            if (gap <= cadence.Max) intervals.Add(gap);
        }

        var medianInterval = intervals.Count > 0 ? Median(intervals.Select(i => (decimal)i)) : cadence.NominalDays;
        var daysSince = today.DayNumber - last.Date.DayNumber;
        var status = Lifecycle(daysSince, medianInterval, o);

        var category = ModeCategory(rows);
        var kind = KindOf(key, chain, category, utility, amount, o);

        var flags = new List<string>();
        var months = o.ForgottenAfterMonths;
        var samePriceForLong = !chain.Variable
            && rows[0].Date <= today.AddMonths(-months)
            && priceSince <= today.AddMonths(-months)
            && rows.Where(r => r.Date >= today.AddMonths(-months)).All(r => Within(r.Amount, amount, o.SamePriceTolerance));
        if (kind == RecurringKinds.Subscription && status == RecurringStatuses.Active && samePriceForLong
            && (amount <= o.ForgottenMaxAmount || MerchantHints.IsSubscription(key)))
        {
            flags.Add(RecurringFlags.Forgotten);
        }

        if (priceIncrease) flags.Add(RecurringFlags.PriceIncrease);
        if (rows[0].Date >= today.AddDays(-o.NewWithinDays)) flags.Add(RecurringFlags.New);

        var next = cadence.Name switch
        {
            RecurringCadences.Monthly => last.Date.AddMonths(1),
            RecurringCadences.Yearly => last.Date.AddYears(1),
            _ => last.Date.AddDays(7),
        };
        var users = rows.Select(r => r.Row.UserId).Distinct().ToList();
        var confidence = cadence.Name == RecurringCadences.Yearly && rows.Count < 3 ? RecurringConfidences.Medium : RecurringConfidences.High;

        var facts = new RecurringStreamFacts(
            DisplayName: DisplayName(last.Text),
            NameSource: NameSource(rows),
            Kind: kind,
            VariableAmount: chain.Variable,
            Category: category,
            UserId: users.Count == 1 ? users[0] : null,
            MedianAmount: amount,
            LastAmount: last.Amount,
            PreviousAmount: previous,
            AnnualCost: decimal.Round(amount * cadence.PerYear, 2, MidpointRounding.AwayFromZero),
            Occurrences: rows.Count,
            MissedCount: chain.Missed,
            FirstSeenLocal: rows[0].Date,
            LastSeenLocal: last.Date,
            NextExpectedLocal: status == RecurringStatuses.Stopped ? null : next,
            Status: status,
            Flags: flags,
            Confidence: confidence,
            InstallmentNumber: null,
            InstallmentTotal: null,
            RemainingAmount: null,
            EndMonth: null);
        return new DetectedStream(key, cadence.Name, facts, rows.Select(r => r.Row.Id).ToList());
    }

    private static string KindOf(string key, Chain chain, string category, bool utility, decimal amount, RecurrenceOptions o)
    {
        if (chain.Cadence.Name == RecurringCadences.Weekly) return RecurringKinds.Habit;
        if (MerchantHints.IsSubscription(key)) return RecurringKinds.Subscription;
        if (chain.Variable || utility || category is "MORADIA" or "SAUDE") return RecurringKinds.FixedBill;
        if (category is "LAZER" or "COMPRAS" or "OUTROS" && amount <= o.SubscriptionMaxAmount) return RecurringKinds.Subscription;
        // A fixed amount in a fixed rhythm that is not a subscription (school, insurance, a monthly pass): a fixed bill.
        return RecurringKinds.FixedBill;
    }

    private static string Lifecycle(int daysSince, decimal medianInterval, RecurrenceOptions o)
    {
        if (daysSince >= o.StoppedAfterIntervals * medianInterval) return RecurringStatuses.Stopped;
        if (daysSince >= o.DormantAfterIntervals * medianInterval) return RecurringStatuses.SuspectedDormant;
        return RecurringStatuses.Active;
    }

    // ---------------------------------------------------------------- small frequent spend (3.2)

    private static DetectedStream? DetectFrequentSmallSpend(string key, List<Keyed> pool, DateOnly today, RecurrenceOptions o)
    {
        var from = today.AddDays(-(o.HabitWindowDays - 1));
        var window = pool.Where(r => r.Date >= from).ToList();
        if (window.Count < o.HabitMinOccurrences) return null;
        var median = Median(window.Select(r => r.Amount));
        if (median > o.HabitMaxMedian) return null;

        var users = window.Select(r => r.Row.UserId).Distinct().ToList();
        var flags = new List<string>();
        if (pool[0].Date >= today.AddDays(-o.NewWithinDays)) flags.Add(RecurringFlags.New);
        var facts = new RecurringStreamFacts(
            DisplayName: DisplayName(window[^1].Text),
            NameSource: NameSource(window),
            Kind: RecurringKinds.Habit,
            VariableAmount: false,
            Category: ModeCategory(window),
            UserId: users.Count == 1 ? users[0] : null,
            MedianAmount: median,
            LastAmount: window[^1].Amount,
            PreviousAmount: null,
            // What the last 30 days cost, kept up for 12 months.
            AnnualCost: window.Sum(r => r.Amount) * 12,
            Occurrences: window.Count,
            MissedCount: 0,
            FirstSeenLocal: window[0].Date,
            LastSeenLocal: window[^1].Date,
            NextExpectedLocal: null,
            Status: RecurringStatuses.Active,
            Flags: flags,
            Confidence: RecurringConfidences.High,
            InstallmentNumber: null,
            InstallmentTotal: null,
            RemainingAmount: null,
            EndMonth: null);
        return new DetectedStream(key, RecurringCadences.Irregular, facts, window.Select(r => r.Row.Id).ToList());
    }

    // ---------------------------------------------------------------- amounts

    private const int MaxClustersPerKey = 8;

    /// <summary>
    /// Groups of charges whose amounts are all within <paramref name="tolerance"/> of the median of the group, the
    /// biggest group first. Each charge is in one group at most.
    /// </summary>
    private static List<List<Keyed>> Cluster(List<Keyed> rows, decimal tolerance)
    {
        var clusters = new List<List<Keyed>>();
        var remaining = rows.OrderBy(r => r.Amount).ToList();
        while (remaining.Count > 0 && clusters.Count < MaxClustersPerKey)
        {
            // The centre that gathers most charges (two pointers over the sorted amounts).
            var bestCount = 0;
            var bestCentre = remaining[0].Amount;
            int lo = 0, hi = 0;
            for (var i = 0; i < remaining.Count; i++)
            {
                var centre = remaining[i].Amount;
                while (remaining[lo].Amount < centre * (1 - tolerance)) lo++;
                if (hi < i) hi = i;
                while (hi + 1 < remaining.Count && remaining[hi + 1].Amount <= centre * (1 + tolerance)) hi++;
                if (hi - lo + 1 > bestCount)
                {
                    bestCount = hi - lo + 1;
                    bestCentre = centre;
                }
            }

            var members = remaining.Where(r => Within(r.Amount, bestCentre, tolerance)).ToList();
            for (var pass = 0; pass < 4; pass++)
            {
                var median = Median(members.Select(r => r.Amount));
                var next = remaining.Where(r => Within(r.Amount, median, tolerance)).ToList();
                if (next.Count == members.Count && next.Zip(members).All(p => ReferenceEquals(p.First, p.Second))) break;
                if (next.Count == 0) break;
                members = next;
            }

            var finalMedian = Median(members.Select(r => r.Amount));
            members = members.Where(r => Within(r.Amount, finalMedian, tolerance)).ToList();
            if (members.Count == 0) members = [remaining[0]];

            var ids = members.Select(m => m.Row.Id).ToHashSet();
            remaining.RemoveAll(r => ids.Contains(r.Row.Id));
            clusters.Add(members.OrderBy(r => r.Date).ThenBy(r => r.Row.TimestampUtc).ThenBy(r => r.Row.Id).ToList());
        }

        return clusters;
    }

    private static bool Within(decimal amount, decimal centre, decimal tolerance)
        => amount >= centre * (1 - tolerance) && amount <= centre * (1 + tolerance);

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0m;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : decimal.Round((sorted[mid - 1] + sorted[mid]) / 2, 2, MidpointRounding.AwayFromZero);
    }

    // ---------------------------------------------------------------- dates

    private enum LinkKind
    {
        None,
        Regular,
        Missed,
    }

    private static LinkKind Link(CadenceSpec cadence, int gap)
    {
        if (gap >= cadence.Min && gap <= cadence.Max) return LinkKind.Regular;
        if (cadence.MissedMin is { } min && cadence.MissedMax is { } max && gap >= min && gap <= max) return LinkKind.Missed;
        return LinkKind.None;
    }

    /// <summary>
    /// The series of the cluster (charges of similar amount of one establishment) in the cadence. Usually one: the
    /// longest chain, when it holds enough of the charges of its period. Two services of the same amount (the same
    /// plan paid by each person of the couple, two subscriptions of the same price in an app shop) are told apart
    /// in two ways, and only for the monthly and the yearly cadences:
    /// by person — every person with charges has a series of their own, and the series run side by side;
    /// by day — up to <see cref="RecurrenceOptions.MaxSeriesPerAmount"/> series, each always charged on its own day
    /// of the month, that together leave almost nothing out. A purchase every 10 or 14 days fits neither.
    /// </summary>
    private static List<List<Keyed>> SeriesOf(List<Keyed> cluster, CadenceSpec cadence, RecurrenceOptions o)
    {
        var splits = cadence.Name != RecurringCadences.Weekly && o.MaxSeriesPerAmount > 1;
        if (splits && SeriesByPerson(cluster, cadence, o) is { } byPerson) return byPerson;

        var first = RawChain(cluster, cadence);
        if (first.Count < cadence.MinOccurrences) return [];

        if (splits)
        {
            var found = new List<List<Keyed>> { first };
            var rest = Without(cluster, first);
            while (found.Count < o.MaxSeriesPerAmount)
            {
                var next = RawChain(rest, cadence);
                if (next.Count < cadence.MinOccurrences) break;
                found.Add(next);
                rest = Without(rest, next);
            }

            // Each series is measured against what is in no other series: what is left over is what must be rare.
            if (found.Count > 1
                && found.All(series => OnTheSameDay(series, o)
                                       && Covers(series, Without(cluster, found.Where(other => !ReferenceEquals(other, series)).SelectMany(other => other)), o)))
            {
                return found;
            }
        }

        return Covers(first, cluster, o) ? [first] : [];
    }

    private static List<List<Keyed>>? SeriesByPerson(List<Keyed> cluster, CadenceSpec cadence, RecurrenceOptions o)
    {
        var people = cluster.GroupBy(r => r.Row.UserId).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        if (people.Count < 2) return null;

        var series = new List<List<Keyed>>();
        foreach (var charges in people)
        {
            var chain = RawChain(charges, cadence);
            if (chain.Count < cadence.MinOccurrences || !Covers(chain, charges, o)) return null;
            series.Add(chain);
        }

        // Side by side: a series that only starts when the other ended is one service whose charges changed hands.
        var latestStart = series.Max(chain => chain[0].Date);
        var earliestEnd = series.Min(chain => chain[^1].Date);
        return latestStart < earliestEnd ? series : null;
    }

    private static List<Keyed> Without(List<Keyed> rows, IEnumerable<Keyed> taken)
    {
        var ids = taken.Select(r => r.Row.Id).ToHashSet();
        return rows.Where(r => !ids.Contains(r.Row.Id)).ToList();
    }

    /// <summary>True when every charge of the series is within a few days of the day of the month of the first one.</summary>
    private static bool OnTheSameDay(List<Keyed> series, RecurrenceOptions o)
    {
        var first = series[0].Date;
        foreach (var charge in series)
        {
            var months = (charge.Date.Year - first.Year) * 12 + charge.Date.Month - first.Month;
            var distance = Enumerable.Range(months - 1, 3).Min(m => Math.Abs(charge.Date.DayNumber - first.AddMonths(m).DayNumber));
            if (distance > o.SameDayToleranceDays) return false;
        }

        return true;
    }

    private static bool Covers(List<Keyed> chain, List<Keyed> cluster, RecurrenceOptions o)
    {
        if (chain.Count == 0) return false;
        var days = cluster.Where(r => r.Date >= chain[0].Date && r.Date <= chain[^1].Date).Select(r => r.Date).Distinct().Count();
        return chain.Count >= o.MinChainCoverage * days;
    }

    /// <summary>
    /// The longest run of charges of the cluster in the rhythm of the cadence, one per day: each step is a regular
    /// interval, or one skipped charge (about twice the interval) — never two skipped charges in a row. Among runs
    /// of the same length, the one that ends later.
    /// </summary>
    private static List<Keyed> RawChain(List<Keyed> cluster, CadenceSpec cadence)
    {
        var nodes = cluster.GroupBy(r => r.Date).OrderBy(g => g.Key).Select(g => g.First()).ToList();
        var n = nodes.Count;
        if (n == 0) return [];

        // length[i, s]: the longest chain ending at i whose last step was regular or a start (s = 0) or a skipped charge (s = 1).
        var length = new int[n, 2];
        var previous = new (int Index, int State)[n, 2];
        var reach = cadence.MissedMax ?? cadence.Max;
        var start = 0;
        for (var i = 0; i < n; i++)
        {
            length[i, 0] = 1;
            previous[i, 0] = (-1, 0);
            length[i, 1] = 0;
            previous[i, 1] = (-1, 0);
            while (nodes[i].Date.DayNumber - nodes[start].Date.DayNumber > reach) start++;
            for (var j = start; j < i; j++)
            {
                var link = Link(cadence, nodes[i].Date.DayNumber - nodes[j].Date.DayNumber);
                if (link == LinkKind.Regular)
                {
                    for (var s = 0; s < 2; s++)
                    {
                        if (length[j, s] == 0 || length[j, s] + 1 <= length[i, 0]) continue;
                        length[i, 0] = length[j, s] + 1;
                        previous[i, 0] = (j, s);
                    }
                }
                else if (link == LinkKind.Missed && length[j, 0] + 1 > length[i, 1])
                {
                    length[i, 1] = length[j, 0] + 1;
                    previous[i, 1] = (j, 0);
                }
            }
        }

        int bestIndex = 0, bestState = 0;
        for (var i = 0; i < n; i++)
        {
            for (var s = 1; s >= 0; s--)
            {
                // Longer wins; of the same length, the one that ends later (and, at the same end, without a skipped charge).
                if (length[i, s] == 0 || length[i, s] < length[bestIndex, bestState]) continue;
                bestIndex = i;
                bestState = s;
            }
        }

        var chain = new List<Keyed>();
        var (index, state) = (bestIndex, bestState);
        while (index >= 0)
        {
            chain.Add(nodes[index]);
            (index, state) = previous[index, state];
        }

        chain.Reverse();
        return chain;
    }

    private static int CountMissed(List<Keyed> chain, CadenceSpec cadence)
    {
        var missed = 0;
        for (var i = 1; i < chain.Count; i++)
        {
            if (chain[i].Date.DayNumber - chain[i - 1].Date.DayNumber > cadence.Max) missed++;
        }

        return missed;
    }

    // ---------------------------------------------------------------- small things

    private static string ModeCategory(IEnumerable<Keyed> rows)
        => rows
            .GroupBy(r => TransactionCategories.NormalizeOrOther(r.Row.Category), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(r => r.Date))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First().Key;

    private static string DisplayName(string text)
        => MerchantKey.Truncate(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), RecurringStream.MaxDisplayNameLength);

    /// <summary>"description" as soon as one charge of the stream took its text from a description: the safe side for what may go to a provider.</summary>
    private static string NameSource(IEnumerable<Keyed> rows)
        => rows.Any(r => r.FromDescription) ? RecurringNameSources.Description : RecurringNameSources.Merchant;

    private static int MonthIndex(DateOnly date) => date.Year * 12 + date.Month - 1;

    private static string MonthText(int monthIndex) => $"{monthIndex / 12:D4}-{monthIndex % 12 + 1:D2}";
}
