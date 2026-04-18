using CoupleSync.Application.AiChat;

namespace CoupleSync.UnitTests.AiChat;

[Trait("Category", "AiChat")]
public sealed class ChatRateLimiterTests
{
    [Fact]
    public void AllowsRequestsWithinLimit()
    {
        var limiter = new ChatRateLimiter();
        var coupleId = Guid.NewGuid();

        for (var i = 0; i < 30; i++)
        {
            Assert.True(limiter.IsAllowed(coupleId), $"Request {i + 1} should be allowed.");
        }
    }

    [Fact]
    public void RejectsRequestAfterLimit()
    {
        var limiter = new ChatRateLimiter();
        var coupleId = Guid.NewGuid();

        for (var i = 0; i < 30; i++)
            limiter.IsAllowed(coupleId);

        Assert.False(limiter.IsAllowed(coupleId));
    }

    [Fact]
    public void IsolatesBetweenCouples()
    {
        var limiter = new ChatRateLimiter();
        var coupleA = Guid.NewGuid();
        var coupleB = Guid.NewGuid();

        for (var i = 0; i < 30; i++)
            limiter.IsAllowed(coupleA);

        // Couple A is exhausted
        Assert.False(limiter.IsAllowed(coupleA));

        // Couple B should still be allowed
        Assert.True(limiter.IsAllowed(coupleB));
    }

    [Fact]
    public void ResetsAfterWindowExpires()
    {
        var limiter = new ChatRateLimiter();
        var coupleId = Guid.NewGuid();

        // Fill the queue with timestamps older than the 1-hour window via reflection
        var requestsField = typeof(ChatRateLimiter)
            .GetField("_requests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<Guid, System.Collections.Generic.Queue<DateTime>>)requestsField.GetValue(limiter)!;

        var expired = DateTime.UtcNow.AddHours(-2);
        var queue = new System.Collections.Generic.Queue<DateTime>();
        for (var i = 0; i < 30; i++)
            queue.Enqueue(expired);
        dict.TryAdd(coupleId, queue);

        // After window expiry the limiter should allow new requests
        Assert.True(limiter.IsAllowed(coupleId));
    }
}
