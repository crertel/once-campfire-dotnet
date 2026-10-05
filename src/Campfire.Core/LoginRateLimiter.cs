using System.Collections.Concurrent;

namespace Campfire.Core;

public sealed class LoginRateLimiter
{
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new();

    public LoginRateLimiter(TimeProvider time) => _time = time;

    public int Limit { get; init; } = 10;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(3);

    public bool TryAcquire(string key)
    {
        var now = _time.GetUtcNow();
        var queue = _attempts.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= Window)
                queue.Dequeue();
            if (queue.Count >= Limit)
                return false;
            queue.Enqueue(now);
            return true;
        }
    }
}
