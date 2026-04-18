using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public sealed class NotificationEvent : ICoupleScoped
{
    private NotificationEvent() { }

    private NotificationEvent(
        Guid id,
        Guid coupleId,
        Guid userId,
        string alertType,
        string title,
        string body,
        DateTime createdAtUtc)
    {
        Id = id;
        CoupleId = coupleId;
        UserId = userId;
        AlertType = alertType;
        Title = title;
        Body = body;
        Status = "Pending";
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid UserId { get; private set; }
    public string AlertType { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public string Status { get; private set; } = "Pending";
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? DeliveredAtUtc { get; private set; }

    public static NotificationEvent Create(
        Guid coupleId,
        Guid userId,
        string alertType,
        string title,
        string body,
        DateTime nowUtc)
    {
        if (title.Length > 128) throw new ArgumentException("Title must be at most 128 characters.", nameof(title));
        if (body.Length > 512) throw new ArgumentException("Body must be at most 512 characters.", nameof(body));

        return new NotificationEvent(Guid.NewGuid(), coupleId, userId, alertType, title, body, nowUtc);
    }

    public void MarkDelivered(DateTime nowUtc)
    {
        Status = "Delivered";
        DeliveredAtUtc = nowUtc;
    }

    public void MarkFailed()
    {
        Status = "Failed";
    }
}
