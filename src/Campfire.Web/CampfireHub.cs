using Campfire.Core;
using Microsoft.AspNetCore.SignalR;

namespace Campfire.Web;

public sealed class CampfireHub(CampfireApp app, LiveConnections connections) : Hub
{
    public static string RoomGroup(long roomId) => $"room:{roomId}";

    public static string UserGroup(long userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var token = Context.GetHttpContext()?.Request.Cookies[SessionCookies.Name];
        var user = await app.UserFromTokenAsync(token);
        if (user is null)
        {
            Context.Abort();
            return;
        }

        Context.Items["userId"] = user.Id;
        Context.Items["rooms"] = new HashSet<long>();
        connections.Add(user.Id, Context);
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(user.Id));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items["userId"] is long userId)
        {
            connections.Remove(userId, Context.ConnectionId);
            foreach (var roomId in Rooms())
                await app.DisconnectAsync(userId, roomId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinRoom(long roomId)
    {
        var userId = RequireUser();
        await app.ConnectAsync(userId, roomId);
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomId));
        Rooms().Add(roomId);
        await Clients.Group(RoomGroup(roomId)).SendAsync("Presence", new LivePresence
        {
            RoomId = roomId,
            UserId = userId,
            Present = true,
        });
    }

    public async Task LeaveRoom(long roomId)
    {
        var userId = RequireUser();
        Rooms().Remove(roomId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId));
        await app.DisconnectAsync(userId, roomId);
        await Clients.Group(RoomGroup(roomId)).SendAsync("Presence", new LivePresence
        {
            RoomId = roomId,
            UserId = userId,
            Present = false,
        });
    }

    private long RequireUser() =>
        Context.Items["userId"] is long userId ? userId : throw new HubException("Sign in required.");

    private HashSet<long> Rooms()
    {
        if (Context.Items["rooms"] is HashSet<long> rooms)
            return rooms;
        rooms = [];
        Context.Items["rooms"] = rooms;
        return rooms;
    }
}
