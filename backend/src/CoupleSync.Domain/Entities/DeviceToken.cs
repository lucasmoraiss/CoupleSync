using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public sealed class DeviceToken : ICoupleScoped
{
    private DeviceToken() { }

    private DeviceToken(
        Guid id,
        Guid userId,
        Guid coupleId,
        string token,
        string platform,
        DateTime nowUtc)
    {
        Id = id;
        UserId = userId;
        CoupleId = coupleId;
        Token = token;
        Platform = platform;
        LastSeenAtUtc = nowUtc;
        CreatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid CoupleId { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public string Platform { get; private set; } = string.Empty;
    public DateTime LastSeenAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static DeviceToken Create(Guid userId, Guid coupleId, string token, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token must not be null or empty.", nameof(token));

        return new DeviceToken(Guid.NewGuid(), userId, coupleId, token, "android", nowUtc);
    }

    public void UpdateLastSeen(string token, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token must not be null or empty.", nameof(token));

        Token = token;
        LastSeenAtUtc = nowUtc;
    }
}
