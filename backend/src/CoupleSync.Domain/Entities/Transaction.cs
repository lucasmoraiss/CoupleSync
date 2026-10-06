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
        DateTime createdAtUtc,
        TransactionSource source)
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
        Source = source;
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
    public TransactionSource Source { get; private set; }
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
        DateTime createdAtUtc,
        TransactionSource source = TransactionSource.Manual)
    {
        return new Transaction(
            Guid.NewGuid(), coupleId, userId, fingerprint, bank,
            amount, currency, eventTimestampUtc, description, merchant,
            category, ingestEventId, createdAtUtc, source);
    }

    public void UpdateCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category) || category.Length > 64)
            throw new ArgumentException("A categoria é obrigatória e deve ter no máximo 64 caracteres.", nameof(category));
        Category = category;
    }

    /// <summary>
    /// Edits the user-facing fields. The fingerprint and the ingest event are never touched, so a transaction
    /// that came from an import keeps the identity the duplicate detection knows it by.
    /// </summary>
    public void Edit(decimal? amount, string? description, DateTime? eventTimestampUtc, string? category, string? merchant = null)
    {
        if (amount is <= 0)
            throw new ArgumentException("O valor deve ser maior que zero.", nameof(amount));
        if (description is { Length: > 512 })
            throw new ArgumentException("A descrição deve ter no máximo 512 caracteres.", nameof(description));
        if (merchant is { Length: > 512 })
            throw new ArgumentException("O estabelecimento deve ter no máximo 512 caracteres.", nameof(merchant));
        if (category is not null)
            UpdateCategory(category);

        if (amount.HasValue) Amount = amount.Value;
        if (description is not null) Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (merchant is not null) Merchant = string.IsNullOrWhiteSpace(merchant) ? null : merchant.Trim();
        if (eventTimestampUtc.HasValue) EventTimestampUtc = eventTimestampUtc.Value;
    }

    public void LinkToGoal(Guid? goalId)
    {
        GoalId = goalId;
    }
}
