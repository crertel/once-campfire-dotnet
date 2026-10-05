using Campfire.Core;
using Microsoft.AspNetCore.SignalR;

namespace Campfire.Web;

public sealed class SignalRRealtime(IHubContext<CampfireHub> hub, LiveConnections connections) : ICampfireRealtime
{
    public Task MessageAsync(LiveMessage message, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(CampfireHub.RoomGroup(message.RoomId)).SendAsync("Message", message, cancellationToken);

    public Task RemovedAsync(LiveRemoval removal, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(CampfireHub.RoomGroup(removal.RoomId)).SendAsync("Removed", removal, cancellationToken);

    public Task UnreadAsync(long userId, LiveUnread unread, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(CampfireHub.UserGroup(userId)).SendAsync("Unread", unread, cancellationToken);

    public Task PresenceAsync(LivePresence presence, CancellationToken cancellationToken = default) =>
        hub.Clients.Group(CampfireHub.RoomGroup(presence.RoomId)).SendAsync("Presence", presence, cancellationToken);

    public async Task SignedOutAsync(long userId, CancellationToken cancellationToken = default)
    {
        try
        {
            await hub.Clients.Group(CampfireHub.UserGroup(userId)).SendAsync("SignedOut", userId, cancellationToken);
        }
        catch (Exception)
        {
            // The session is still removed when the hub cannot be reached.
        }

        try
        {
            connections.Abort(userId);
        }
        catch (Exception)
        {
            // Abort is best-effort. Logout has already deleted the session row.
        }
    }
}
