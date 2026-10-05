namespace Campfire.Core;

public sealed class LiveMessage
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public long CreatorId { get; set; }
    public string Creator { get; set; } = "";
    public string Html { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class LiveRemoval
{
    public long Id { get; set; }
    public long RoomId { get; set; }
}

public sealed class LiveUnread
{
    public long RoomId { get; set; }
}

public sealed class LivePresence
{
    public long RoomId { get; set; }
    public long UserId { get; set; }
    public bool Present { get; set; }
}

public interface ICampfireRealtime
{
    Task MessageAsync(LiveMessage message, CancellationToken cancellationToken = default);
    Task RemovedAsync(LiveRemoval removal, CancellationToken cancellationToken = default);
    Task UnreadAsync(long userId, LiveUnread unread, CancellationToken cancellationToken = default);
    Task PresenceAsync(LivePresence presence, CancellationToken cancellationToken = default);
    Task SignedOutAsync(long userId, CancellationToken cancellationToken = default);
}

public sealed class NoopRealtime : ICampfireRealtime
{
    public Task MessageAsync(LiveMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RemovedAsync(LiveRemoval removal, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UnreadAsync(long userId, LiveUnread unread, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PresenceAsync(LivePresence presence, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SignedOutAsync(long userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
