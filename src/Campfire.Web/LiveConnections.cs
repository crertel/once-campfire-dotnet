using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Campfire.Web;

public sealed class LiveConnections
{
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, HubCallerContext>> _byUser = new();

    public void Add(long userId, HubCallerContext context)
    {
        var connections = _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, HubCallerContext>());
        connections[context.ConnectionId] = context;
    }

    public void Remove(long userId, string connectionId)
    {
        if (_byUser.TryGetValue(userId, out var connections))
            connections.TryRemove(connectionId, out _);
    }

    public void Abort(long userId)
    {
        if (!_byUser.TryGetValue(userId, out var connections))
            return;

        foreach (var context in connections.Values)
        {
            try
            {
                context.Abort();
            }
            catch (Exception)
            {
                // Logout still deletes the session when a circuit cannot be closed.
            }
        }
    }
}
