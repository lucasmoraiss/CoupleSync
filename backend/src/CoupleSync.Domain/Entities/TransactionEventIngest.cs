using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public enum IngestStatus
{
    Accepted = 1,
    Rejected = 2,
    Duplicate = 3  // used in T-006
}

public sealed class TransactionEventIngest : ICoupleScoped
{
    private TransactionEventIngest() { }

    private TransactionEventIngest(
        Guid id,
        Guid coupleId,
        Guid userId,
        string bank,
        decimal amount,
        string currency,
        DateTime eventTimestamp,
        string? description,
        string? merchant,
        string? rawNotificationTextRedacted,
        DateTime createdAtUtc)
    {
        Id = id;
        CoupleId = coupleId;
        UserId = userId;
        Bank = bank;
        Amount = amount;
        Currency = currency;
        EventTimestamp = eventTimestamp;
        Description = description;
        Merchant = merchant;
        RawNotificationTextRedacted = rawNotificationTextRedacted;
        CreatedAtUtc = createdAtUtc;
        Status = IngestStatus.Accepted;
    }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid UserId { get; private set; }
    public string Bank { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public DateTime EventTimestamp { get; private set; }
    public string? Description { get; private set; }
    public string? Merchant { get; private set; }
    public string? RawNotificationTextRedacted { get; private set; }
    public IngestStatus Status { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static TransactionEventIngest Create(
        Guid coupleId,
        Guid userId,
        string bank,
        decimal amount,
        string currency,
        DateTime eventTimestamp,
        string? description,
        string? merchant,
        string? rawNotificationTextRedacted,
        DateTime createdAtUtc)
    {
        if (coupleId == Guid.Empty) throw new ArgumentException("CoupleId is required.", nameof(coupleId));
        if (userId == Guid.Empty) throw new ArgumentException("UserId is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(bank)) throw new ArgumentException("Bank is required.", nameof(bank));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(currency)) throw new ArgumentException("Currency is required.", nameof(currency));

        return new TransactionEventIngest(
            Guid.NewGuid(), coupleId, userId, bank.Trim().ToUpperInvariant(),
            amount, currency.Trim().ToUpperInvariant(), eventTimestamp,
            description, merchant, rawNotificationTextRedacted, createdAtUtc);
    }

    public void MarkDuplicate()
    {
        Status = IngestStatus.Duplicate;
    }

    public void MarkRejected(string errorMessage)
    {
        Status = IngestStatus.Rejected;
        ErrorMessage = errorMessage;
    }
}
