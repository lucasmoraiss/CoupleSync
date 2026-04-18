using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public enum GoalStatus
{
    Active = 1,
    Archived = 2
}

public sealed class Goal : ICoupleScoped
{
    private Goal() { }

    private Goal(
        Guid id,
        Guid coupleId,
        Guid createdByUserId,
        string title,
        string? description,
        decimal targetAmount,
        string currency,
        DateTime deadline,
        DateTime createdAtUtc)
    {
        Id = id;
        CoupleId = coupleId;
        CreatedByUserId = createdByUserId;
        Title = title;
        Description = description;
        TargetAmount = targetAmount;
        Currency = currency;
        Deadline = deadline;
        Status = GoalStatus.Active;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public decimal TargetAmount { get; private set; }
    public string Currency { get; private set; } = "BRL";
    public DateTime Deadline { get; private set; }
    public GoalStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static Goal Create(
        Guid coupleId,
        Guid createdByUserId,
        string title,
        string? description,
        decimal targetAmount,
        string currency,
        DateTime deadline,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 128)
            throw new ArgumentException("Title must be a non-empty string of at most 128 characters.", nameof(title));

        if (targetAmount <= 0)
            throw new ArgumentException("TargetAmount must be greater than zero.", nameof(targetAmount));

        if (string.IsNullOrWhiteSpace(currency) || currency.Length < 2 || currency.Length > 3)
            throw new ArgumentException("Currency must be 2-3 characters.", nameof(currency));

        if (deadline.Kind == DateTimeKind.Unspecified)
            deadline = DateTime.SpecifyKind(deadline, DateTimeKind.Utc);

        if (deadline <= createdAtUtc)
            throw new ArgumentException("Deadline must be in the future.", nameof(deadline));

        if (description is not null && description.Length > 512)
            throw new ArgumentException("Description must be at most 512 characters.", nameof(description));

        return new Goal(Guid.NewGuid(), coupleId, createdByUserId, title, description, targetAmount, currency, deadline, createdAtUtc);
    }

    public void Archive(DateTime nowUtc)
    {
        if (Status == GoalStatus.Archived) return;
        Status = GoalStatus.Archived;
        UpdatedAtUtc = nowUtc;
    }

    public void Update(string? title, string? description, decimal? targetAmount, DateTime? deadline, DateTime nowUtc)
    {
        if (title is not null)
        {
            if (string.IsNullOrWhiteSpace(title) || title.Length > 128)
                throw new ArgumentException("Title must be a non-empty string of at most 128 characters.", nameof(title));
            Title = title;
        }

        if (description is not null)
        {
            if (description.Length > 512)
                throw new ArgumentException("Description must be at most 512 characters.", nameof(description));
            Description = description;
        }

        if (targetAmount is not null)
        {
            if (targetAmount.Value <= 0)
                throw new ArgumentException("TargetAmount must be greater than zero.", nameof(targetAmount));
            TargetAmount = targetAmount.Value;
        }

        if (deadline is not null)
        {
            var d = deadline.Value;
            if (d.Kind == DateTimeKind.Unspecified)
                d = DateTime.SpecifyKind(d, DateTimeKind.Utc);
            if (d <= nowUtc)
                throw new ArgumentException("Deadline must be a future date.", nameof(deadline));
            Deadline = d;
        }

        UpdatedAtUtc = nowUtc;
    }
}
