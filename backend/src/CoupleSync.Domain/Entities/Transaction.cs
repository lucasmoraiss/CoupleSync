using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public sealed class Transaction : ICoupleScoped
{
    private Transaction() { }

    private Transaction(
        Guid id,
        Guid coupleId,
        Guid userId,
        string fingerprint,
        string bank,
        decimal amount,
        string currency,
        DateTime eventTimestampUtc,
        string? description,
        string? merchant,
        string category,
        Guid ingestEventId,
        DateTime createdAtUtc)
    {
        Id = id;
        CoupleId = coupleId;
        UserId = userId;
        Fingerprint = fingerprint;
        Bank = bank;
        Amount = amount;
        Currency = currency;
        EventTimestampUtc = eventTimestampUtc;
        Description = description;
        Merchant = merchant;
        Category = category;
        IngestEventId = ingestEventId;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid UserId { get; private set; }
    public string Fingerprint { get; private set; } = string.Empty;
    public string Bank { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public DateTime EventTimestampUtc { get; private set; }
    public string? Description { get; private set; }
    public string? Merchant { get; private set; }
    public string Category { get; private set; } = string.Empty;
    public Guid IngestEventId { get; private set; }
    public Guid? GoalId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Transaction Create(
        Guid coupleId,
        Guid userId,
        string fingerprint,
        string bank,
        decimal amount,
        string currency,
        DateTime eventTimestampUtc,
        string? description,
        string? merchant,
        string category,
        Guid ingestEventId,
        DateTime createdAtUtc)
    {
        return new Transaction(
            Guid.NewGuid(), coupleId, userId, fingerprint, bank,
            amount, currency, eventTimestampUtc, description, merchant,
            category, ingestEventId, createdAtUtc);
    }

    public void UpdateCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category) || category.Length > 64)
            throw new ArgumentException("Category must be a non-empty string of at most 64 characters.", nameof(category));
        Category = category;
    }

    public void LinkToGoal(Guid? goalId)
    {
        GoalId = goalId;
    }
}
