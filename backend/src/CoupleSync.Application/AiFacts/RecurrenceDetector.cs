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

    /// <summary>How far back the detector reads for everything but the yearly cadence.</summary>
    public int HistoryMonths { get; set; } = 13;

    /// <summary>
    /// How far back the detector reads to find a yearly stream: two charges up to 395 days apart, the last one up
    /// to 395 days ago. What is older than <see cref="HistoryMonths"/> counts for nothing else.
    /// </summary>
    public int YearlyHistoryMonths { get; set; } = 26;

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

    /// <summary>
    /// How many days a charge may be away from the day of the month of its series for the series to count as
    /// "always on its own day" — what tells two bills of the same amount from a purchase made twice a month.
    /// </summary>
    public int SameDayToleranceDays { get; set; } = 1;

    /// <summary>
    /// A charge of the same amount (within <see cref="SamePriceTolerance"/>) at most this many days from a charge
    /// of a series is read as that same charge, seen twice (the phone of each person, a statement and a
    /// notification) — unless the two were registered by the same person through the same means on different days:
    /// those are two charges. The series of two people are one stream only when every charge of one of them is
    /// such a copy of a charge of the other.
    /// </summary>
    public int SameChargeWithinDays { get; set; } = 4;

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

    // ---- proof: what makes a series a commitment that counts in the total (design 3.3, "prova proporcional ao movimento")

    /// <summary>
    /// A monthly series is charged "on its own day" when all its charges fit in a window of this many days of the
    /// month (the end of the month counts as one day: the 31st, the 30th and the 28th of February are the same day).
    /// </summary>
    public int OwnDayWindowDays { get; set; } = 7;

    /// <summary>
    /// An establishment "only charges that" when the series holds at least this share of the days with a purchase
    /// there, in the period of the series.
    /// </summary>
    public decimal ExclusiveShare { get; set; } = 0.8m;

    /// <summary>
    /// In an establishment with other purchases, a series of one price on its own day counts in the total only with
    /// this many charges. With fewer it is shown as possible.
    /// </summary>
    public int SamePriceMinCharges { get; set; } = 5;

    /// <summary>
    /// An establishment is "busy" when it has more than this many other days with a purchase for each charge of the
    /// series: a coincidence is more likely there, so more proof is asked. 0 switches the rule off.
    /// </summary>
    public decimal BusyPlaceRatio { get; set; } = 2m;

    /// <summary>How many charges more than <see cref="SamePriceMinCharges"/> a busy establishment asks for the total.</summary>
    public int BusyPlaceExtraCharges { get; set; } = 1;

    /// <summary>In a busy establishment a series of one price on its own day is only shown as possible with this many charges.</summary>
    public int BusyPlacePossibleMinCharges { get; set; } = 4;

    /// <summary>The charges of a yearly series are "one year apart" within this many days.</summary>
    public int YearlyAnniversaryDays { get; set; } = 15;
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

/// <summary>
/// One stream found: its key (without the suffix that tells two streams of the same shop apart), its facts and its
/// charges. A stream with a pattern but without proof is "possible": its confidence is
/// <see cref="RecurringConfidences.Low"/>, it is shown apart and never counts in the total.
/// </summary>
public sealed record DetectedStream(string MerchantKey, string Cadence, RecurringStreamFacts Facts, IReadOnlyList<Guid> TransactionIds)
{
    /// <summary>
    /// Charges read as copies of the ones in <see cref="TransactionIds"/> (the same charge seen twice): they are of
    /// the stream, are not counted and are not a new charge of it.
    /// </summary>
    public IReadOnlyList<Guid> CopyTransactionIds { get; init; } = [];
}

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

    /// <param name="Echoes">Charges read as copies of the ones in <paramref name="Rows"/>: they are of the stream, and are not counted.</param>
    private sealed record Chain(CadenceSpec Cadence, List<Keyed> Rows, int Missed, bool Variable, List<Keyed> Echoes)
    {
        public Proof Proof { get; init; } = Proof.Confirmed;

        /// <summary>The charges of the other series found side by side with this one, among charges of the same amount (the plan of the other person).</summary>
        public HashSet<Guid>? Siblings { get; init; }
    }

    /// <summary>
    /// How much proof a series has. <see cref="Confirmed"/> counts in the total; <see cref="Possible"/> is only
    /// shown, waiting for the person to say what it is; <see cref="None"/> is a shopping habit, and is not shown.
    /// </summary>
    private enum Proof
    {
        None,
        Possible,
        Confirmed,
    }

    /// <summary>One series of a cluster, and the charges of the cluster read as copies of its charges.</summary>
    private sealed record Series(List<Keyed> Rows, List<Keyed> Echoes);

    /// <param name="saidRecurring">
    /// The charges of each stream the person said is recurring ("it is a subscription", "it is a fixed bill"): the
    /// series that holds most of the charges of one of them is confirmed without any other proof.
    /// </param>
    public static DetectionResult Detect(
        IReadOnlyList<RecurrenceRow> rows, DateOnly today, RecurrenceOptions options, IReadOnlyCollection<IReadOnlyCollection<Guid>>? saidRecurring = null)
    {
        var pinned = (saidRecurring ?? []).Where(charges => charges.Count > 0).Select(charges => charges.ToHashSet()).ToList();
        var keyed = new List<Keyed>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Amount <= 0 || row.LocalDate > today) continue;
            var text = row.Establishment;
            if (text is null) continue;
            if (PersonTransferRule.IsPersonTransfer(row.Merchant, row.Description, text)) continue;

            // A name read from a description (a statement line, a note typed by the person) is free text: with a
            // document, a phone, an e-mail or a Pix key in it the charge forms no stream at all. A bare long number
            // that is no valid CPF nor CNPJ and has no word saying what it is (a client code) only leaves the name.
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

        // What is older than the months of history is only read to find a yearly stream.
        var recentFrom = today.AddMonths(-options.HistoryMonths);
        var streams = new List<DetectedStream>();
        var consumed = new HashSet<Guid>();
        DetectInstallments(keyed.Where(k => k.Date >= recentFrom).ToList(), today, options, streams, consumed);

        foreach (var group in keyed.GroupBy(k => k.Key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var all = group.OrderBy(k => k.Date).ThenBy(k => k.Row.TimestampUtc).ThenBy(k => k.Row.Id).ToList();
            var pool = all.Where(k => k.Date >= recentFrom && !consumed.Contains(k.Row.Id)).ToList();
            var older = all.Where(k => k.Date < recentFrom).ToList();
            if (pool.Count == 0 && older.Count == 0) continue;

            var found = DetectCadences(group.Key, pool, older, today, options, pinned);
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

            // Two purchases of the same plan in the same shop and month count as one commitment. The same part
            // seen twice (the phone of each person, a statement and a notification: the same number, the same
            // amount, another person or another means) is one part, and is counted once.
            var counted = new List<Keyed>();
            foreach (var part in parts.Where(p => p.Number == last.Number).Select(p => p.Row))
            {
                var copy = counted.Any(other => Within(part.Amount, other.Amount, options.SamePriceTolerance)
                                                && (part.Row.UserId != other.Row.UserId || part.Row.Source != other.Row.Source));
                if (!copy) counted.Add(part);
            }

            var amount = counted.Sum(p => p.Amount);
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

    /// <summary>A series that may be a stream, and the band of amounts it was found in.</summary>
    private sealed record Candidate(Chain Chain, Band Band);

    /// <summary>
    /// Where a candidate series came from. <see cref="BillBand"/>: the wide band of a "variable fixed bill", in an
    /// establishment that is a utility or whose category is MORADIA or SAUDE — looked at before everything else.
    /// <see cref="PriceBand"/>: charges of similar amount (also across one step of price). <see cref="AnyAmount"/>:
    /// the wide band in any other establishment, only for a series whose amount does vary.
    /// </summary>
    private enum Band
    {
        BillBand,
        PriceBand,
        AnyAmount,
    }

    /// <summary>
    /// The streams of one establishment. Every series in the rhythm of a cadence is a candidate; what decides is its
    /// proof (<see cref="ProofOf"/>): the confirmed ones are chosen first, then the possible ones among what is
    /// left, and a series without proof is a shopping habit and is not a stream.
    /// </summary>
    /// <param name="pool">The charges of the establishment in the months of history.</param>
    /// <param name="older">Its charges before that: only the yearly cadence reads them.</param>
    /// <param name="pinned">The charges of each stream the person said is recurring.</param>
    private static List<DetectedStream> DetectCadences(
        string key, List<Keyed> pool, List<Keyed> older, DateOnly today, RecurrenceOptions o, List<HashSet<Guid>> pinned)
    {
        var monthly = new CadenceSpec(RecurringCadences.Monthly, 25, 34, 53, 65, 30, o.MonthlyMinOccurrences, 12m);
        var weekly = new CadenceSpec(RecurringCadences.Weekly, 5, 9, 12, 16, 7, o.WeeklyMinOccurrences, 52m);
        var yearly = new CadenceSpec(RecurringCadences.Yearly, 335, 395, null, null, 365, o.YearlyMinOccurrences, 1m);

        var category = ModeCategory(pool.Count > 0 ? pool : older);
        var utility = MerchantHints.IsUtility(key);
        var knownName = utility || MerchantHints.IsSubscription(key);
        var billPlace = utility || category is "MORADIA" or "SAUDE";
        var everything = older.Concat(pool).ToList();
        var candidates = new List<Candidate>();

        void AddSeries(List<Keyed> cluster, CadenceSpec cadence, Band band)
        {
            var found = SeriesOf(cluster, cadence, o);
            foreach (var series in found)
            {
                var median = Median(series.Rows.Select(r => r.Amount));
                var variable = band != Band.PriceBand && series.Rows.Any(r => !Within(r.Amount, median, o.AmountTolerance));
                if (band == Band.AnyAmount && !variable) continue;   // a steady amount is found in its own band

                // The charges of the series next to it (the plan of the other person) are not "other purchases" of this one.
                var siblings = found.Count < 2
                    ? null
                    : found.Where(other => !ReferenceEquals(other, series)).SelectMany(other => other.Rows.Concat(other.Echoes)).Select(r => r.Row.Id).ToHashSet();
                candidates.Add(new Candidate(
                    new Chain(cadence, series.Rows, CountMissed(series.Rows, cadence), variable, series.Echoes) { Siblings = siblings }, band));
            }
        }

        // The band of a "variable fixed bill" (electricity, water...): its amounts may be far from each other.
        foreach (var cluster in Cluster(pool, o.VariableBillTolerance)) AddSeries(cluster, monthly, billPlace ? Band.BillBand : Band.AnyAmount);

        // Charges of similar amount, in every cadence, plus one step of price.
        var clusters = Cluster(pool, o.AmountTolerance);
        // The yearly cadence also reads what is older than the months of history.
        var yearlyClusters = older.Count == 0 ? clusters : Cluster(everything, o.AmountTolerance);
        foreach (var cadence in new[] { monthly, weekly, yearly })
        {
            var raw = new List<(List<Keyed> Chain, List<Keyed> Cluster)>();
            foreach (var cluster in ReferenceEquals(cadence, yearly) ? yearlyClusters : clusters)
            {
                var chain = RawChain(cluster, cadence);
                if (chain.Count == 0) continue;
                raw.Add((chain, cluster));
                AddSeries(cluster, cadence, Band.PriceBand);
            }

            // One step of price: the old price in one cluster, the new one in another, the dates in the same rhythm.
            // Only when one of the two sides is a series by itself: two pairs of purchases of two prices are not
            // "the same bill, that got more expensive".
            bool IsSeries((List<Keyed> Chain, List<Keyed> Cluster) side)
                => side.Chain.Count >= cadence.MinOccurrences && Covers(side.Chain, side.Cluster, SameChargeDays(cadence, o), o);
            foreach (var newer in raw.Where(c => c.Chain.Count >= 2))
            {
                var before = raw
                    .Where(c => c.Chain.Count >= 2 && c.Chain[^1].Date < newer.Chain[0].Date
                                && Link(cadence, newer.Chain[0].Date.DayNumber - c.Chain[^1].Date.DayNumber) != LinkKind.None)
                    .OrderByDescending(c => c.Chain[^1].Date)
                    .FirstOrDefault();
                if (before.Chain is null) continue;
                if (!IsSeries(newer) && !IsSeries(before)) continue;
                var merged = before.Chain.Concat(newer.Chain).ToList();
                var echoes = EchoesOf(merged, before.Cluster.Concat(newer.Cluster).ToList(), SameChargeDays(cadence, o), o);
                candidates.Add(new Candidate(new Chain(cadence, merged, CountMissed(merged, cadence), false, echoes), Band.PriceBand));
            }
        }

        // The same price to the cent (within the "same price" band): the series of a subscription in a shop where
        // loose purchases of a near amount would otherwise be mixed with its charges.
        var samePrice = Cluster(pool, o.SamePriceTolerance);
        var samePriceYearly = older.Count == 0 ? samePrice : Cluster(everything, o.SamePriceTolerance);
        foreach (var cadence in new[] { monthly, yearly })
        {
            foreach (var cluster in ReferenceEquals(cadence, yearly) ? samePriceYearly : samePrice) AddSeries(cluster, cadence, Band.PriceBand);
        }

        var ordered = candidates
            .Where(c => c.Chain.Rows.Count >= c.Chain.Cadence.MinOccurrences)
            // A yearly charge whose renewal is late is not a yearly charge any more.
            .Where(c => !ReferenceEquals(c.Chain.Cadence, yearly) || today.DayNumber - c.Chain.Rows[^1].Date.DayNumber <= YearlyMaxIntervalDays)
            .OrderBy(c => c.Band == Band.BillBand ? 0 : 1)
            .ThenByDescending(c => c.Chain.Rows.Count)
            .ThenBy(c => c.Band)
            .ThenBy(c => c.Chain.Cadence.NominalDays == 30 ? 0 : c.Chain.Cadence.NominalDays)
            .ThenByDescending(c => c.Chain.Rows[^1].Date)
            .Select(c => c.Chain)
            .ToList();

        var chosen = new List<Chain>();
        // The charges of the streams chosen so far, with their copies: they form no other stream, and for another
        // series of the establishment they are not "other purchases".
        var used = new HashSet<Guid>();

        Proof Judge(Chain candidate)
            => IsSaidRecurring(candidate, everything, pinned) ? Proof.Confirmed : ProofOf(candidate, everything, used, knownName, o);

        void Choose(Chain candidate, Proof proof)
        {
            foreach (var r in candidate.Rows.Concat(candidate.Echoes)) used.Add(r.Row.Id);
            chosen.Add(candidate with { Proof = proof });
        }

        // Each confirmed stream takes its charges out of the "other purchases" of the establishment, which may be
        // what another series needed to be confirmed: again, until nothing changes.
        for (var progress = true; progress;)
        {
            progress = false;
            foreach (var candidate in ordered)
            {
                if (candidate.Rows.Any(r => used.Contains(r.Row.Id)) || Judge(candidate) != Proof.Confirmed) continue;
                Choose(candidate, Proof.Confirmed);
                progress = true;
            }
        }

        var confirmed = used.ToHashSet();
        foreach (var candidate in ordered)
        {
            if (candidate.Rows.Any(r => used.Contains(r.Row.Id)) || ProofOf(candidate, everything, confirmed, knownName, o) != Proof.Possible) continue;
            Choose(candidate, Proof.Possible);
        }

        return chosen
            .OrderBy(c => c.Rows[0].Date).ThenBy(c => c.Rows[0].Amount).ThenBy(c => c.Rows[0].Row.Id)
            .Select(c => Build(key, c, utility, today, o))
            .ToList();
    }

    /// <summary>True when the series holds most of the charges (the ones still read) of a stream the person said is recurring.</summary>
    private static bool IsSaidRecurring(Chain candidate, List<Keyed> everything, List<HashSet<Guid>> pinned)
    {
        if (pinned.Count == 0) return false;
        var ofSeries = candidate.Rows.Concat(candidate.Echoes).Select(r => r.Row.Id).ToHashSet();
        foreach (var charges in pinned)
        {
            var stillRead = everything.Count(r => charges.Contains(r.Row.Id));
            if (stillRead > 0 && 2 * ofSeries.Count(charges.Contains) > stillRead) return true;
        }

        return false;
    }

    /// <summary>
    /// The proof of a series, proportional to the movement of the establishment (design 3.3):
    /// <list type="bullet">
    /// <item>the establishment only charges that (<see cref="RecurrenceOptions.ExclusiveShare"/>) and the series is
    /// on its own day (<see cref="RecurrenceOptions.OwnDayWindowDays"/>) → confirmed, also when the amount varies,
    /// in any category; with a name of the lists of subscriptions and utilities the day may even move;</item>
    /// <item>the establishment also has loose purchases: one price (one step accepted), on its own day, that price
    /// not seen out of that day, for <see cref="RecurrenceOptions.SamePriceMinCharges"/> charges (more in a busy
    /// establishment) → confirmed; with fewer charges → possible;</item>
    /// <item>the establishment only charges that, on days that move → possible;</item>
    /// <item>anything else is a shopping habit: no proof.</item>
    /// </list>
    /// Yearly: confirmed when the establishment only charges that, at one price, one year apart; possible when it
    /// only charges that. Weekly: always a habit, listed apart and never in the total.
    /// </summary>
    /// <param name="taken">The charges of the streams already chosen: they are not "other purchases" of this series.</param>
    private static Proof ProofOf(Chain chain, List<Keyed> everything, HashSet<Guid> taken, bool knownName, RecurrenceOptions o)
    {
        if (chain.Cadence.Name == RecurringCadences.Weekly) return Proof.Confirmed;

        var rows = chain.Rows;
        var charges = rows.Count;
        var within = SameChargeDays(chain.Cadence, o);
        var isMonthly = chain.Cadence.Name == RecurringCadences.Monthly;
        var ofSeries = rows.Select(r => r.Row.Id).ToHashSet();
        var lowest = rows.Min(r => r.Amount) * (1 - o.SamePriceTolerance);
        var highest = rows.Max(r => r.Amount) * (1 + o.SamePriceTolerance);

        bool SamePriceAsSeries(Keyed r) => rows.Any(x => Within(r.Amount, x.Amount, o.SamePriceTolerance));

        // A charge of the price of the series, on the day of the series, that the rhythm left out (a gap of 35
        // days) is a charge of the series: it is not "another purchase" of the establishment.
        bool LeftOutByTheRhythm(Keyed r)
        {
            if (!isMonthly || ofSeries.Contains(r.Row.Id)) return false;
            var priceOfSeries = chain.Variable ? r.Amount >= lowest && r.Amount <= highest : SamePriceAsSeries(r);
            return priceOfSeries && OnItsOwnDayOfTheMonth(rows.Append(r).ToList(), o);
        }

        var purchases = everything
            .Where(r => ofSeries.Contains(r.Row.Id) || (!taken.Contains(r.Row.Id) && chain.Siblings?.Contains(r.Row.Id) != true && !LeftOutByTheRhythm(r)))
            .ToList();
        var otherDays = OtherDays(rows, purchases, within, o);
        var onlyChargesThat = charges >= o.ExclusiveShare * (charges + otherDays);

        if (chain.Cadence.Name == RecurringCadences.Yearly)
        {
            var median = Median(rows.Select(r => r.Amount));
            var onePriceAYear = rows.All(r => Within(r.Amount, median, o.SamePriceTolerance));
            var aYearApart = true;
            for (var i = 1; i < charges; i++)
                aYearApart &= Math.Abs(rows[i].Date.DayNumber - rows[i - 1].Date.DayNumber - 365) <= o.YearlyAnniversaryDays;
            if (!onlyChargesThat) return Proof.None;
            return onePriceAYear && aYearApart ? Proof.Confirmed : Proof.Possible;
        }

        var onItsDay = OnItsOwnDayOfTheMonth(rows, o);
        if (onlyChargesThat && (onItsDay || knownName)) return Proof.Confirmed;

        // The price of the series is (almost) only seen on the day of the series.
        var priceOnlyOnItsDay = charges >= o.ExclusiveShare * (charges + OtherDays(rows, purchases.Where(SamePriceAsSeries).ToList(), within, o));
        if (OnePriceWithOneStep(rows, o.SamePriceTolerance) && onItsDay && priceOnlyOnItsDay)
        {
            var busy = o.BusyPlaceRatio > 0 && otherDays > o.BusyPlaceRatio * charges;
            if (charges >= o.SamePriceMinCharges + (busy ? o.BusyPlaceExtraCharges : 0)) return Proof.Confirmed;
            if (!busy || charges >= o.BusyPlacePossibleMinCharges) return Proof.Possible;
        }

        return onlyChargesThat ? Proof.Possible : Proof.None;
    }

    /// <summary>One price (within the tolerance, around the median) — or two, one after the other: one step of price.</summary>
    private static bool OnePriceWithOneStep(List<Keyed> rows, decimal tolerance)
    {
        bool OnePrice(List<Keyed> part)
        {
            if (part.Count == 0) return true;
            var median = Median(part.Select(r => r.Amount));
            return part.All(r => Within(r.Amount, median, tolerance));
        }

        for (var split = 0; split < rows.Count; split++)
        {
            if (OnePrice(rows.Take(split).ToList()) && OnePrice(rows.Skip(split).ToList())) return true;
        }

        return false;
    }

    /// <summary>True when every charge falls in one window of <see cref="RecurrenceOptions.OwnDayWindowDays"/> days of the month.</summary>
    private static bool OnItsOwnDayOfTheMonth(List<Keyed> rows, RecurrenceOptions o) => DaysOfTheMonthSpanned(rows) < o.OwnDayWindowDays;

    /// <summary>
    /// How far apart, in days, the charges are within the month (0 = always the same day). Each charge is measured
    /// from the day of the month of one of them, in its own month or in the ones next to it, so the end of the month
    /// is one day (the 31st, the 30th, the 28th of February) and a window may cross from one month to the next.
    /// </summary>
    private static int DaysOfTheMonthSpanned(List<Keyed> rows)
    {
        var best = int.MaxValue;
        foreach (var reference in rows)
        {
            int earliest = 0, latest = 0;
            foreach (var charge in rows)
            {
                var months = (charge.Date.Year - reference.Date.Year) * 12 + charge.Date.Month - reference.Date.Month;
                var offset = int.MaxValue;
                for (var m = months - 1; m <= months + 1; m++)
                {
                    var days = charge.Date.DayNumber - reference.Date.AddMonths(m).DayNumber;
                    if (Math.Abs((long)days) < Math.Abs((long)offset)) offset = days;
                }

                earliest = Math.Min(earliest, offset);
                latest = Math.Max(latest, offset);
            }

            best = Math.Min(best, latest - earliest);
        }

        return best;
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
        // A charge registered by more than one person (its copies) is of nobody in particular.
        var users = rows.Concat(chain.Echoes).Select(r => r.Row.UserId).Distinct().ToList();
        var confidence = chain.Proof == Proof.Possible ? RecurringConfidences.Low
            : cadence.Name == RecurringCadences.Yearly && rows.Count < 3 ? RecurringConfidences.Medium : RecurringConfidences.High;
        // "Forgotten", "got more expensive" and "new" are said of what is a commitment: a possible one says nothing yet.
        if (chain.Proof == Proof.Possible) flags.Clear();

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
        return new DetectedStream(key, cadence.Name, facts, rows.Select(r => r.Row.Id).ToList())
        {
            CopyTransactionIds = chain.Echoes.Select(r => r.Row.Id).ToList(),
        };
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
    /// longest chain, when it holds enough of the charges of its period. A charge of the cluster that is a charge
    /// of the chain seen twice (<see cref="SameCharge"/>: the same amount, at most
    /// <see cref="RecurrenceOptions.SameChargeWithinDays"/> days apart, not registered twice by the same person
    /// through the same means) goes with the series and does not count.
    /// Two services of the same amount (the same plan paid by each person of the couple, two subscriptions of the
    /// same price in an app shop) are two series only for the monthly and the yearly cadences:
    /// by person — every person with charges has a series of their own and the series run side by side; the series
    /// of two people are one only when every charge of one of them is a charge of the other seen twice (one
    /// service registered by both);
    /// by day — up to <see cref="RecurrenceOptions.MaxSeriesPerAmount"/> series, each always charged on its own day
    /// of the month, no charge of one being a charge of another seen twice, that together leave almost nothing out
    /// (nothing at all, when the series had to be looked for by their day of the month).
    /// In doubt, one series (or none): a purchase every 10 or 14 days, or twice a month on days that move, fits neither.
    /// </summary>
    private static List<Series> SeriesOf(List<Keyed> cluster, CadenceSpec cadence, RecurrenceOptions o)
    {
        var within = SameChargeDays(cadence, o);
        var splits = cadence.Name != RecurringCadences.Weekly && o.MaxSeriesPerAmount > 1;
        if (splits && SeriesByPerson(cluster, cadence, o, within) is { } byPerson) return byPerson;

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
                && found.All(series => OnItsOwnDay(series, o))
                && Apart(found, within, o)
                && found.All(series => Covers(series, Without(cluster, found.Where(other => !ReferenceEquals(other, series)).SelectMany(other => other)), within, o)))
            {
                return found.Select(series => new Series(series, EchoesOf(series, rest, within, o))).ToList();
            }

            // Two series on days close to each other (the 3rd and the 6th) may come out of the longest chains mixed,
            // a charge of one in the chain of the other. They are looked for again by their day of the month, and
            // accepted only when they are apart and leave nothing of their period out.
            var byDay = new List<List<Keyed>>();
            var left = cluster;
            while (byDay.Count < o.MaxSeriesPerAmount)
            {
                var next = ChainOnOneDay(left, cadence, o);
                if (next.Count < cadence.MinOccurrences) break;
                byDay.Add(next);
                left = Without(left, next);
            }

            if (byDay.Count > 1
                // Side by side: a series that only starts when the other ended is one service whose day changed.
                && byDay.Max(series => series[0].Date) < byDay.Min(series => series[^1].Date)
                && Apart(byDay, within, o)
                && byDay.All(series => OtherDays(series, Without(cluster, byDay.Where(other => !ReferenceEquals(other, series)).SelectMany(other => other)), within, o) == 0))
            {
                return byDay.Select(series => new Series(series, EchoesOf(series, left, within, o))).ToList();
            }
        }

        return Covers(first, cluster, within, o) ? [new Series(first, EchoesOf(first, cluster, within, o))] : [];
    }

    /// <summary>How close two charges are to be read as one. The weekly cadence has no such reading: only the same day.</summary>
    private static int SameChargeDays(CadenceSpec cadence, RecurrenceOptions o)
        => cadence.Name == RecurringCadences.Weekly ? 0 : Math.Max(0, o.SameChargeWithinDays);

    private static List<Series>? SeriesByPerson(List<Keyed> cluster, CadenceSpec cadence, RecurrenceOptions o, int within)
    {
        var people = cluster.GroupBy(r => r.Row.UserId).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        if (people.Count < 2) return null;

        var series = new List<List<Keyed>>();
        foreach (var charges in people)
        {
            var chain = RawChain(charges, cadence);
            if (chain.Count < cadence.MinOccurrences || !Covers(chain, charges, within, o)) return null;
            series.Add(chain);
        }

        // Side by side: a series that only starts when the other ended is one service whose charges changed hands.
        var latestStart = series.Max(chain => chain[0].Date);
        var earliestEnd = series.Min(chain => chain[^1].Date);
        if (latestStart >= earliestEnd) return null;

        // The series of two people are one only when they coincide: every charge of one of them is a charge of the
        // other seen twice (the phone of each one, a shared card). One pair of close charges, or a pair in some of
        // the months only, leaves them as they are: the plan of each person.
        var together = new List<List<int>>();
        for (var i = 0; i < series.Count; i++)
        {
            var joined = together.Where(group => group.Any(j => Coincide(series[i], series[j], within, o))).ToList();
            together.RemoveAll(joined.Contains);
            together.Add(joined.SelectMany(group => group).Append(i).ToList());
        }

        var result = new List<Series>();
        foreach (var group in together.OrderBy(group => group.Min()))
        {
            if (group.Count == 1)
            {
                result.Add(new Series(series[group[0]], EchoesOf(series[group[0]], people[group[0]], within, o)));
                continue;
            }

            // One series, one charge a month; the charges next to its charges are the copies.
            var users = group.Select(index => people[index][0].Row.UserId).ToHashSet();
            var charges = cluster.Where(r => users.Contains(r.Row.UserId)).ToList();
            var one = RawChain(charges, cadence);
            result.Add(new Series(one, EchoesOf(one, charges, within, o)));
        }

        return result;
    }

    /// <summary>True when every charge of one of the two series is a charge of the other one seen twice.</summary>
    private static bool Coincide(List<Keyed> one, List<Keyed> other, int within, RecurrenceOptions o)
        => one.All(charge => Near(charge, other, within, o)) || other.All(charge => Near(charge, one, within, o));

    /// <summary>
    /// The longest chain of the cluster whose charges are all on the day of the month of one of them (at most
    /// <see cref="RecurrenceOptions.SameDayToleranceDays"/> days from it). Among chains of the same length, the one
    /// of the earliest day of the month.
    /// </summary>
    private static List<Keyed> ChainOnOneDay(List<Keyed> cluster, CadenceSpec cadence, RecurrenceOptions o)
    {
        var best = new List<Keyed>();
        // A chain has one charge per day, the first of the day (as in RawChain): the others need not be looked at.
        var days = cluster.GroupBy(r => r.Date).Select(g => g.First()).ToList();
        // The distance to the day of a charge only depends on its day of the month: one reference per day is enough.
        foreach (var reference in days.GroupBy(r => r.Date.Day).OrderBy(g => g.Key).Select(g => g.First()))
        {
            var onItsDay = days.Where(charge => DaysFromTheDayOf(reference.Date, charge.Date) <= o.SameDayToleranceDays).ToList();
            if (onItsDay.Count <= best.Count) continue;
            var chain = RawChain(onItsDay, cadence);
            if (chain.Count > best.Count) best = chain;
        }

        return best;
    }

    private static List<Keyed> Without(List<Keyed> rows, IEnumerable<Keyed> taken)
    {
        var ids = taken.Select(r => r.Row.Id).ToHashSet();
        return rows.Where(r => !ids.Contains(r.Row.Id)).ToList();
    }

    /// <summary>
    /// True when the series is always charged on its own day of the month: there is a charge whose day every other
    /// one is at most <see cref="RecurrenceOptions.SameDayToleranceDays"/> days from.
    /// </summary>
    private static bool OnItsOwnDay(List<Keyed> series, RecurrenceOptions o)
        => series.Any(reference => series.All(charge => DaysFromTheDayOf(reference.Date, charge.Date) <= o.SameDayToleranceDays));

    /// <summary>How far a date is from the day of the month of another one, in its own month or in the ones next to it.</summary>
    private static int DaysFromTheDayOf(DateOnly reference, DateOnly date)
    {
        var months = (date.Year - reference.Year) * 12 + date.Month - reference.Month;
        var days = int.MaxValue;
        for (var m = months - 1; m <= months + 1; m++) days = Math.Min(days, Math.Abs(date.DayNumber - reference.AddMonths(m).DayNumber));
        return days;
    }

    /// <summary>True when no charge of a series is a charge of another one seen twice (<see cref="SameCharge"/>).</summary>
    private static bool Apart(List<List<Keyed>> series, int within, RecurrenceOptions o)
    {
        for (var i = 0; i < series.Count; i++)
        {
            for (var j = i + 1; j < series.Count; j++)
            {
                if (series[i].Any(charge => Near(charge, series[j], within, o))) return false;
            }
        }

        return true;
    }

    /// <summary>True when the charge is one of the charges of the chain seen twice.</summary>
    private static bool Near(Keyed charge, List<Keyed> chain, int within, RecurrenceOptions o)
    {
        foreach (var other in chain)
        {
            if (SameCharge(charge, other, within, o)) return true;
        }

        return false;
    }

    /// <summary>
    /// One charge seen twice: the same amount (within <see cref="RecurrenceOptions.SamePriceTolerance"/>, which
    /// holds tax and rounding), at most <paramref name="within"/> days apart, and — on different days — not
    /// registered by the same person through the same means: the same phone does not capture one charge twice, so
    /// those are two charges.
    /// </summary>
    private static bool SameCharge(Keyed charge, Keyed other, int within, RecurrenceOptions o)
    {
        var days = Math.Abs(charge.Date.DayNumber - other.Date.DayNumber);
        if (days > within || !Within(charge.Amount, other.Amount, o.SamePriceTolerance)) return false;
        return days == 0 || charge.Row.UserId != other.Row.UserId || charge.Row.Source != other.Row.Source;
    }

    /// <summary>The charges of the cluster that are not of the chain and are read as copies of its charges.</summary>
    private static List<Keyed> EchoesOf(List<Keyed> chain, List<Keyed> cluster, int within, RecurrenceOptions o)
    {
        var ids = chain.Select(r => r.Row.Id).ToHashSet();
        return cluster.Where(r => !ids.Contains(r.Row.Id) && Near(r, chain, within, o)).ToList();
    }

    /// <summary>
    /// True when the chain holds enough of the days with a charge in its period (<see cref="OtherDays"/> are the
    /// ones it does not hold).
    /// </summary>
    private static bool Covers(List<Keyed> chain, List<Keyed> cluster, int within, RecurrenceOptions o)
        => chain.Count > 0 && chain.Count >= o.MinChainCoverage * (chain.Count + OtherDays(chain, cluster, within, o));

    /// <summary>
    /// How many days of the period of the chain have a charge of the cluster that is not of the chain. A charge on
    /// a day of the chain is the same occurrence, and a charge of the chain seen twice (<see cref="SameCharge"/>)
    /// is not another day.
    /// </summary>
    private static int OtherDays(List<Keyed> chain, List<Keyed> cluster, int within, RecurrenceOptions o)
    {
        var days = chain.Select(r => r.Date).ToHashSet();
        return cluster
            .Where(r => r.Date >= chain[0].Date && r.Date <= chain[^1].Date && !days.Contains(r.Date) && !Near(r, chain, within, o))
            .Select(r => r.Date).Distinct().Count();
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
