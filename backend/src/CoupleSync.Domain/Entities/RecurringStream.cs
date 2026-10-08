using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

/// <summary>What a recurring stream is. <see cref="Installment"/> is a purchase paid in n of N monthly parts.</summary>
public static class RecurringKinds
{
    public const string Subscription = "Subscription";
    public const string FixedBill = "FixedBill";
    public const string Installment = "Installment";
    public const string Habit = "Habit";
}

/// <summary>
/// The rhythm of a stream. <see cref="Irregular"/> is the "small frequent spend" (4 or more purchases in 30 days):
/// it has no fixed interval.
/// </summary>
public static class RecurringCadences
{
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";
    public const string Yearly = "Yearly";
    public const string Irregular = "Irregular";
}

public static class RecurringStatuses
{
    public const string Active = "Active";
    public const string SuspectedDormant = "SuspectedDormant";
    public const string Stopped = "Stopped";
}

public static class RecurringFlags
{
    public const string Forgotten = "Forgotten";
    public const string PriceIncrease = "PriceIncrease";
    public const string New = "New";
    public const string ChargedAfterCancel = "ChargedAfterCancel";
}

public static class RecurringConfidences
{
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";
}

/// <summary>What the person said about a stream. It survives every recalculation.</summary>
public static class RecurringOverrides
{
    public const string NotRecurring = "NotRecurring";
    public const string Cancelled = "Cancelled";
    public const string Subscription = "Subscription";
    public const string FixedBill = "FixedBill";

    public static readonly IReadOnlyList<string> All = [NotRecurring, Cancelled, Subscription, FixedBill];
}

/// <summary>
/// Where the name (and the key) of a stream came from. A name read from a description is shown to the group itself
/// only: it never goes to an AI provider (design 3.8).
/// </summary>
public static class RecurringNameSources
{
    public const string Merchant = "merchant";
    public const string Description = "description";
}

/// <summary>What the detector found for one stream: everything in a row that is recalculated.</summary>
/// <param name="NameSource">One of <see cref="RecurringNameSources"/>.</param>
public sealed record RecurringStreamFacts(
    string DisplayName,
    string NameSource,
    string Kind,
    bool VariableAmount,
    string Category,
    Guid? UserId,
    decimal MedianAmount,
    decimal LastAmount,
    decimal? PreviousAmount,
    decimal AnnualCost,
    int Occurrences,
    int MissedCount,
    DateOnly FirstSeenLocal,
    DateOnly LastSeenLocal,
    DateOnly? NextExpectedLocal,
    string Status,
    IReadOnlyList<string> Flags,
    string Confidence,
    int? InstallmentNumber,
    int? InstallmentTotal,
    decimal? RemainingAmount,
    string? EndMonth);

/// <summary>
/// One thing the group pays again and again (a subscription, a fixed bill, a purchase in instalments, a habit), as
/// the detector found it in the transactions. One row per (group, merchant_key, kind, cadence); recalculated in
/// place, so the id and what the person said about it (<see cref="UserOverride"/>) stay.
/// </summary>
public sealed class RecurringStream : ICoupleScoped
{
    public const int MaxMerchantKeyLength = 160;
    public const int MaxDisplayNameLength = 120;
    public const int MaxNameSourceLength = 16;

    private readonly List<RecurringStreamItem> _items = [];

    private RecurringStream()
    {
    }

    public Guid Id { get; private set; }

    public Guid CoupleId { get; private set; }

    public string MerchantKey { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    /// <see cref="RecurringNameSources"/>: "description" when the text of any charge of the stream came from the
    /// description of the transaction (it had no merchant). Such a name never leaves the API towards a provider.
    /// </summary>
    public string NameSource { get; private set; } = RecurringNameSources.Description;

    /// <summary>The kind the detector gave it. The kind shown is <see cref="EffectiveKind"/>.</summary>
    public string Kind { get; private set; } = string.Empty;

    public bool VariableAmount { get; private set; }

    public string Cadence { get; private set; } = string.Empty;

    public string Category { get; private set; } = string.Empty;

    /// <summary>The person, when every charge of the stream is of the same person.</summary>
    public Guid? UserId { get; private set; }

    /// <summary>The typical amount of today: the median of the current price, or of the last 3 charges when the amount varies.</summary>
    public decimal MedianAmount { get; private set; }

    public decimal LastAmount { get; private set; }

    public decimal? PreviousAmount { get; private set; }

    public decimal AnnualCost { get; private set; }

    public int Occurrences { get; private set; }

    public int MissedCount { get; private set; }

    public DateOnly FirstSeenLocal { get; private set; }

    public DateOnly LastSeenLocal { get; private set; }

    public DateOnly? NextExpectedLocal { get; private set; }

    public string Status { get; private set; } = RecurringStatuses.Active;

    /// <summary>Comma separated <see cref="RecurringFlags"/>.</summary>
    public string Flags { get; private set; } = string.Empty;

    public string Confidence { get; private set; } = RecurringConfidences.High;

    public int? InstallmentNumber { get; private set; }

    public int? InstallmentTotal { get; private set; }

    public decimal? RemainingAmount { get; private set; }

    /// <summary>"yyyy-MM" of the last instalment.</summary>
    public string? EndMonth { get; private set; }

    public string? UserOverride { get; private set; }

    public Guid? OverrideByUserId { get; private set; }

    public DateTime? OverrideAtUtc { get; private set; }

    public DateTime DetectedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public IReadOnlyCollection<RecurringStreamItem> Items => _items;

    public IReadOnlyList<string> FlagList
        => Flags.Length == 0 ? [] : Flags.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The kind as the person sees it: what they said it is, or what the detector found.</summary>
    public string EffectiveKind
        => UserOverride is RecurringOverrides.Subscription or RecurringOverrides.FixedBill ? UserOverride : Kind;

    /// <summary>
    /// Out of the active list: the person said it is not recurring, or cancelled it, or it stopped being charged.
    /// What the person cancelled and was charged again is never hidden — also when the charge came back so late that
    /// the series itself is still "stopped".
    /// </summary>
    public bool IsHidden
        => UserOverride == RecurringOverrides.NotRecurring
           || (!FlagList.Contains(RecurringFlags.ChargedAfterCancel)
               && (UserOverride == RecurringOverrides.Cancelled || Status == RecurringStatuses.Stopped));

    public static RecurringStream Create(Guid coupleId, string merchantKey, string cadence, RecurringStreamFacts facts, DateTime nowUtc)
    {
        var stream = new RecurringStream
        {
            Id = Guid.NewGuid(),
            CoupleId = coupleId,
            MerchantKey = merchantKey,
            Cadence = cadence,
        };
        stream.Apply(facts, nowUtc);
        return stream;
    }

    /// <summary>A recalculation that found the stream again. What the person said about it is not touched.</summary>
    public void Apply(RecurringStreamFacts facts, DateTime nowUtc)
    {
        DisplayName = Truncate(facts.DisplayName, MaxDisplayNameLength);
        NameSource = facts.NameSource == RecurringNameSources.Merchant ? RecurringNameSources.Merchant : RecurringNameSources.Description;
        Kind = facts.Kind;
        VariableAmount = facts.VariableAmount;
        Category = facts.Category;
        UserId = facts.UserId;
        MedianAmount = facts.MedianAmount;
        LastAmount = facts.LastAmount;
        PreviousAmount = facts.PreviousAmount;
        AnnualCost = facts.AnnualCost;
        Occurrences = facts.Occurrences;
        MissedCount = facts.MissedCount;
        FirstSeenLocal = facts.FirstSeenLocal;
        LastSeenLocal = facts.LastSeenLocal;
        NextExpectedLocal = facts.NextExpectedLocal;
        Status = facts.Status;
        var chargedAfterCancel = FlagList.Contains(RecurringFlags.ChargedAfterCancel);
        Flags = string.Join(',', facts.Flags);
        SetChargedAfterCancel(chargedAfterCancel);
        Confidence = facts.Confidence;
        InstallmentNumber = facts.InstallmentNumber;
        InstallmentTotal = facts.InstallmentTotal;
        RemainingAmount = facts.RemainingAmount;
        EndMonth = facts.EndMonth;
        DetectedAtUtc = AsUtc(nowUtc);
        UpdatedAtUtc = AsUtc(nowUtc);
    }

    /// <summary>
    /// A recalculation that did not find the stream any more. The row only stays because the person said something
    /// about it: it is kept as stopped, with the numbers it had.
    /// </summary>
    public void MarkNotDetected(DateTime nowUtc)
    {
        Status = RecurringStatuses.Stopped;
        NextExpectedLocal = null;
        var chargedAfterCancel = FlagList.Contains(RecurringFlags.ChargedAfterCancel);
        Flags = string.Empty;
        SetChargedAfterCancel(chargedAfterCancel);
        _items.Clear();
        DetectedAtUtc = AsUtc(nowUtc);
        UpdatedAtUtc = AsUtc(nowUtc);
    }

    /// <summary>
    /// A recalculation that could not see the stream (a yearly charge has its first charge out of the months read
    /// soon after the renewal) while it is still due: the row stays exactly as it is, only the instant is new.
    /// </summary>
    public void KeepAsDetected(DateTime nowUtc)
    {
        DetectedAtUtc = AsUtc(nowUtc);
        UpdatedAtUtc = AsUtc(nowUtc);
    }

    public void SetChargedAfterCancel(bool charged)
    {
        var flags = FlagList.Where(f => f != RecurringFlags.ChargedAfterCancel).ToList();
        if (charged && UserOverride == RecurringOverrides.Cancelled) flags.Add(RecurringFlags.ChargedAfterCancel);
        Flags = string.Join(',', flags);
    }

    /// <summary>What the person says about the stream; null takes it back.</summary>
    public void SetOverride(string? userOverride, Guid byUserId, DateTime nowUtc)
    {
        if (userOverride is not null && !RecurringOverrides.All.Contains(userOverride))
            throw new ArgumentException("Unknown override.", nameof(userOverride));

        UserOverride = userOverride;
        OverrideByUserId = userOverride is null ? null : byUserId;
        OverrideAtUtc = userOverride is null ? null : AsUtc(nowUtc);
        SetChargedAfterCancel(false);
        UpdatedAtUtc = AsUtc(nowUtc);
    }

    /// <summary>Makes the charges of the stream exactly these transactions.</summary>
    public void SetTransactions(IReadOnlyCollection<Guid> transactionIds)
    {
        var wanted = transactionIds.ToHashSet();
        _items.RemoveAll(i => !wanted.Contains(i.TransactionId));
        var present = _items.Select(i => i.TransactionId).ToHashSet();
        foreach (var id in wanted.Where(id => !present.Contains(id)))
            _items.Add(RecurringStreamItem.Create(CoupleId, Id, id));
    }

    /// <summary>Cut to the size of the column without splitting a surrogate pair (half an emoji is not valid text for the database).</summary>
    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength)];
    }

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>One charge (a transaction) of a stream.</summary>
public sealed class RecurringStreamItem : ICoupleScoped
{
    private RecurringStreamItem()
    {
    }

    public Guid Id { get; private set; }

    public Guid CoupleId { get; private set; }

    public Guid StreamId { get; private set; }

    public Guid TransactionId { get; private set; }

    public static RecurringStreamItem Create(Guid coupleId, Guid streamId, Guid transactionId) => new()
    {
        Id = Guid.NewGuid(),
        CoupleId = coupleId,
        StreamId = streamId,
        TransactionId = transactionId,
    };
}
