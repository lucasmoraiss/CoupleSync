using System.Collections.Concurrent;

namespace CoupleSync.Application.AiChat;

public sealed class ChatRateLimiter
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTime>> _requests = new();
    private const int MaxRequests = 30;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public bool IsAllowed(Guid coupleId)
    {
        var now = DateTime.UtcNow;
        var queue = _requests.GetOrAdd(coupleId, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && (now - queue.Peek()) > Window)
                queue.Dequeue();

            if (queue.Count >= MaxRequests)
                return false;

            queue.Enqueue(now);
            return true;
        }
    }
}
