using System.Net;
using Campfire.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Campfire.Tests;

public sealed class DomainTests
{
    [Fact]
    public async Task First_run_creates_an_administrator_and_one_open_room()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");

        Assert.Equal(UserRole.Administrator, admin.Role);
        var memberships = await world.Db.Memberships.Include(membership => membership.Room).Where(membership => membership.UserId == admin.Id).ToListAsync();
        Assert.Single(memberships);
        Assert.Equal(RoomKind.Open, memberships[0].Room.Kind);
        Assert.Equal("All Talk", memberships[0].Room.Name);
        Assert.Equal("Campfire", (await world.App.AccountAsync()).Name);
        var code = (await world.App.AccountAsync()).JoinCode;
        Assert.Matches("^[A-Za-z0-9]{4}-[A-Za-z0-9]{4}-[A-Za-z0-9]{4}$", code);
    }

    [Fact]
    public async Task A_long_password_is_accepted()
    {
        using var world = new AppWorld();
        var password = new string('x', 250);
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", password);
        var session = await world.App.LoginAsync(admin.EmailAddress!, password, "127.0.0.1", "test");
        Assert.NotNull(session);
    }

    [Fact]
    public async Task Open_rooms_include_every_active_user_and_closed_rooms_do_not()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var jason = await world.App.CreateMemberAsync("Jason", "jason@37signals.com", "secret123456");
        Assert.Equal(1, await world.Db.Memberships.CountAsync(membership => membership.UserId == jason.Id));

        var closed = await world.App.CreateRoomAsync(admin.Id, "Design", RoomKind.Closed, [admin.Id]);
        Assert.False(await world.App.IsMemberAsync(jason.Id, closed.Id));

        var open = await world.App.CreateRoomAsync(admin.Id, "Lobby", RoomKind.Open, null);
        Assert.True(await world.App.IsMemberAsync(jason.Id, open.Id));
        Assert.True(await world.App.IsMemberAsync(admin.Id, open.Id));
    }

    [Fact]
    public async Task Direct_rooms_are_a_singleton_with_everything_involvement_and_keep_their_type()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var jason = await world.App.CreateMemberAsync("Jason", "jason@37signals.com", "secret123456");

        var first = await world.App.FindOrCreateDirectAsync(admin.Id, [jason.Id]);
        var second = await world.App.FindOrCreateDirectAsync(jason.Id, [admin.Id]);
        Assert.Equal(first.Id, second.Id);
        var memberships = await world.Db.Memberships.Where(membership => membership.RoomId == first.Id).ToListAsync();
        Assert.Equal(2, memberships.Count);
        Assert.All(memberships, membership => Assert.Equal(Involvement.Everything, membership.Involvement));

        var error = await Assert.ThrowsAsync<AppException>(() => world.App.ChangeRoomKindAsync(admin.Id, first.Id, RoomKind.Open));
        Assert.Equal("can't be changed for a direct room", error.Message);
    }

    [Fact]
    public async Task Messages_can_be_edited_and_deleted_by_the_creator_or_an_administrator()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var jason = await world.App.CreateMemberAsync("Jason", "jason@37signals.com", "secret123456");
        var room = await world.Db.Rooms.SingleAsync();
        var message = await world.App.CreateMessageAsync(jason.Id, room.Id, "hello", null);
        var kevin = await world.App.CreateMemberAsync("Kevin", "kevin@37signals.com", "secret123456");
        await Assert.ThrowsAsync<AppException>(() => world.App.UpdateMessageAsync(kevin.Id, room.Id, message.Id, "nope"));

        var closed = await world.App.CreateRoomAsync(jason.Id, "Notes", RoomKind.Closed, [jason.Id]);
        var note = await world.App.CreateMessageAsync(jason.Id, closed.Id, "secret", null);
        await Assert.ThrowsAsync<AppException>(() => world.App.DeleteMessageAsync(kevin.Id, closed.Id, note.Id));
        await world.App.DeleteMessageAsync(admin.Id, closed.Id, note.Id);
        Assert.False(await world.Db.Messages.AnyAsync(item => item.Id == note.Id));

        var updated = await world.App.UpdateMessageAsync(jason.Id, room.Id, message.Id, "hello again");
        Assert.Contains("hello again", updated.PlainText);
    }

    [Fact]
    public async Task Pagination_returns_the_latest_page_and_exclusive_cursors()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var room = await world.Db.Rooms.SingleAsync();
        var empty = await world.App.MessagesPageAsync(room.Id, null, null);
        Assert.False(empty.RoomHasMessages);

        Message? first = null;
        Message? middle = null;
        Message? last = null;
        for (var i = 1; i <= 3; i++)
        {
            var message = await world.App.CreateMessageAsync(admin.Id, room.Id, $"number {i}", null);
            message.CreatedAt = world.Time.Now.UtcDateTime;
            await world.Db.SaveChangesAsync();
            world.Time.Advance(TimeSpan.FromMinutes(1));
            first ??= message;
            if (i == 2)
                middle = message;
            last = message;
        }

        var page = await world.App.MessagesPageAsync(room.Id, null, null);
        Assert.True(page.RoomHasMessages);
        Assert.Equal(last!.Id, page.Messages[^1].Id);
        var before = await world.App.MessagesPageAsync(room.Id, last.Id, null);
        Assert.True(before.RoomHasMessages);
        Assert.DoesNotContain(before.Messages, message => message.Id == last.Id);
        Assert.Contains(before.Messages, message => message.Id == first!.Id);
        var after = await world.App.MessagesPageAsync(room.Id, null, first!.Id);
        Assert.Contains(after.Messages, message => message.Id == middle!.Id);
        Assert.DoesNotContain(after.Messages, message => message.Id == first.Id);
    }

    [Fact]
    public async Task Unread_is_stamped_only_for_disconnected_visible_members_other_than_the_creator()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var jason = await world.App.CreateMemberAsync("Jason", "jason@37signals.com", "secret123456");
        var kevin = await world.App.CreateMemberAsync("Kevin", "kevin@37signals.com", "secret123456");
        var room = await world.Db.Rooms.SingleAsync();
        await world.App.ConnectAsync(jason.Id, room.Id);
        await world.App.SetInvolvementAsync(kevin.Id, room.Id, Involvement.Invisible);
        world.Realtime.Unreads.Clear();

        await world.App.CreateMessageAsync(admin.Id, room.Id, "ping", null);

        var memberships = await world.Db.Memberships.Where(membership => membership.RoomId == room.Id).ToListAsync();
        Assert.Null(memberships.Single(membership => membership.UserId == admin.Id).UnreadAt);
        Assert.Null(memberships.Single(membership => membership.UserId == jason.Id).UnreadAt);
        Assert.Null(memberships.Single(membership => membership.UserId == kevin.Id).UnreadAt);

        await world.App.DisconnectAsync(jason.Id, room.Id);
        await world.App.SetInvolvementAsync(kevin.Id, room.Id, Involvement.Mentions);
        world.Realtime.Unreads.Clear();
        await world.App.CreateMessageAsync(admin.Id, room.Id, "again", null);
        memberships = await world.Db.Memberships.Where(membership => membership.RoomId == room.Id).ToListAsync();
        Assert.NotNull(memberships.Single(membership => membership.UserId == jason.Id).UnreadAt);
        Assert.Equal(3, world.Realtime.Unreads.Count(item => item.Unread.RoomId == room.Id));
        Assert.Contains(world.Realtime.Messages, message => message.Text == "again");
    }

    [Fact]
    public async Task Connection_counts_expire_after_the_ttl()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var room = await world.Db.Rooms.SingleAsync();
        await world.App.ConnectAsync(admin.Id, room.Id);
        await world.App.ConnectAsync(admin.Id, room.Id);
        var membership = await world.Db.Memberships.SingleAsync();
        Assert.Equal(2, membership.Connections);
        Assert.True(CampfireApp.IsConnected(membership, world.Time.Now.UtcDateTime));

        await world.App.DisconnectAsync(admin.Id, room.Id);
        membership = await world.Db.Memberships.SingleAsync();
        Assert.Equal(1, membership.Connections);
        Assert.NotNull(membership.ConnectedAt);

        world.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(CampfireApp.IsConnected(membership, world.Time.Now.UtcDateTime));
        await world.App.DisconnectAsync(admin.Id, room.Id);
        membership = await world.Db.Memberships.SingleAsync();
        Assert.Equal(0, membership.Connections);
        Assert.Null(membership.ConnectedAt);

        await world.App.RefreshConnectionAsync(admin.Id, room.Id);
        membership = await world.Db.Memberships.SingleAsync();
        Assert.Equal(1, membership.Connections);
        Assert.True(CampfireApp.IsConnected(membership, world.Time.Now.UtcDateTime));
    }

    [Fact]
    public async Task Search_stems_plain_text_and_follows_create_update_and_delete()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var room = await world.Db.Rooms.SingleAsync();
        var eels = await world.App.CreateMessageAsync(admin.Id, room.Id, "<span>My hovercraft is full of eels</span>", null);
        Assert.DoesNotContain(await world.App.SearchAsync(admin.Id, "span"), message => message.Id == eels.Id);
        Assert.Contains(await world.App.SearchAsync(admin.Id, "eel"), message => message.Id == eels.Id);

        await world.App.UpdateMessageAsync(admin.Id, room.Id, eels.Id, "My hovercraft is full of sharks");
        Assert.DoesNotContain(await world.App.SearchAsync(admin.Id, "eel"), message => message.Id == eels.Id);
        Assert.Contains(await world.App.SearchAsync(admin.Id, "sharks"), message => message.Id == eels.Id);

        await world.App.CreateMessageAsync(admin.Id, room.Id, "first cat", null);
        world.Time.Advance(TimeSpan.FromSeconds(1));
        await world.App.CreateMessageAsync(admin.Id, room.Id, "second cat", null);
        world.Time.Advance(TimeSpan.FromSeconds(1));
        await world.App.CreateMessageAsync(admin.Id, room.Id, "third cat", null);
        world.Time.Advance(TimeSpan.FromSeconds(1));
        await world.App.CreateMessageAsync(admin.Id, room.Id, "cat cat cat", null);
        var cats = await world.App.SearchAsync(admin.Id, "cat");
        Assert.Equal(["first cat", "second cat", "third cat", "cat cat cat"], cats.Select(message => message.PlainText).ToArray());

        await world.App.DeleteMessageAsync(admin.Id, room.Id, eels.Id);
        Assert.DoesNotContain(await world.App.SearchAsync(admin.Id, "shark"), message => message.Id == eels.Id);
        Assert.True(await world.Db.SearchQueries.AnyAsync(query => query.UserId == admin.Id));
    }

    [Fact]
    public async Task Mentions_and_drafts_survive_edits_and_room_changes()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var jason = await world.App.CreateMemberAsync("Jason", "jason@37signals.com", "secret123456");
        var room = await world.Db.Rooms.SingleAsync();
        var other = await world.App.CreateRoomAsync(admin.Id, "Other", RoomKind.Open, null);
        var message = await world.App.CreateMessageAsync(admin.Id, room.Id, "Hello @Jason", null);
        Assert.Contains($"data-user-id=\"{jason.Id}\"", message.Html);
        var edited = await world.App.UpdateMessageAsync(admin.Id, room.Id, message.Id, message.Html + " still");
        Assert.Contains($"data-user-id=\"{jason.Id}\"", edited.Html);

        await world.App.SaveDraftAsync(admin.Id, room.Id, "unsent");
        await world.App.SaveDraftAsync(admin.Id, other.Id, "elsewhere");
        await world.App.CreateBoostAsync(jason.Id, room.Id, message.Id, "👍");
        Assert.Equal("unsent", await world.App.DraftAsync(admin.Id, room.Id));
        Assert.Equal("elsewhere", await world.App.DraftAsync(admin.Id, other.Id));
    }

    [Fact]
    public async Task Boosts_reject_more_than_sixteen_characters()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var room = await world.Db.Rooms.SingleAsync();
        var message = await world.App.CreateMessageAsync(admin.Id, room.Id, "hi", null);
        var error = await Assert.ThrowsAsync<AppException>(() => world.App.CreateBoostAsync(admin.Id, room.Id, message.Id, new string('a', 17)));
        Assert.Contains("16", error.Message);
        var boost = await world.App.CreateBoostAsync(admin.Id, room.Id, message.Id, "nice");
        await world.App.DeleteBoostAsync(admin.Id, room.Id, message.Id);
        Assert.False(await world.Db.Boosts.AnyAsync(item => item.Id == boost.Id));
    }

    [Fact]
    public async Task Ban_unban_bots_and_deactivation_follow_the_membership_rules()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var jason = await world.App.CreateMemberAsync("Jason", "jason@37signals.com", "secret123456");
        var direct = await world.App.FindOrCreateDirectAsync(admin.Id, [jason.Id]);
        var room = await world.Db.Rooms.FirstAsync(item => item.Kind == RoomKind.Open);
        await world.App.CreateMessageAsync(jason.Id, room.Id, "goodbye", null);
        var session = await world.App.LoginAsync(jason.EmailAddress!, "secret123456", "203.0.113.8", "test");
        await world.App.SearchAsync(jason.Id, "goodbye");

        await world.App.BanAsync(admin.Id, jason.Id);
        var banned = await world.Db.Users.SingleAsync(user => user.Id == jason.Id);
        Assert.Equal(UserStatus.Banned, banned.Status);
        Assert.Contains(await world.Db.Bans.ToListAsync(), ban => ban.IpAddress == "203.0.113.8");
        Assert.False(await world.Db.Messages.AnyAsync(message => message.CreatorId == jason.Id));
        Assert.Null(await world.Db.Sessions.FindAsync(session!.Id));

        await world.App.UnbanAsync(admin.Id, jason.Id);
        Assert.Equal(UserStatus.Active, (await world.Db.Users.SingleAsync(user => user.Id == jason.Id)).Status);
        Assert.Empty(await world.Db.Bans.ToListAsync());

        var bot = await world.App.CreateBotAsync(admin.Id, "Campbot", "https://example.com/hook");
        Assert.Equal(UserRole.Bot, bot.Role);
        Assert.Equal(12, bot.BotToken!.Length);
        Assert.Equal(bot.Id, (await world.App.AuthenticateBotAsync(CampfireApp.BotKey(bot)))!.Id);
        var previous = bot.BotToken;
        var reset = await world.App.ResetBotKeyAsync(admin.Id, bot.Id);
        Assert.NotEqual(previous, reset.BotToken);
        Assert.Null(await world.App.AuthenticateBotAsync($"{bot.Id}-{previous}"));

        await world.App.AddPushSubscriptionAsync(admin.Id, "https://fcm.googleapis.com/fcm/send/abc", "p256", "auth", "test", host => [IPAddress.Parse("93.184.216.34")]);
        await world.App.DeactivateAsync(admin.Id);
        var deactivated = await world.Db.Users.SingleAsync(user => user.Id == admin.Id);
        Assert.Equal(UserStatus.Deactivated, deactivated.Status);
        Assert.Contains("-deactivated-", deactivated.EmailAddress);
        Assert.True(await world.App.IsMemberAsync(admin.Id, direct.Id));
        Assert.False(await world.App.IsMemberAsync(admin.Id, room.Id));
        Assert.Empty(await world.Db.PushSubscriptions.Where(subscription => subscription.UserId == admin.Id).ToListAsync());
        Assert.Empty(await world.Db.SearchQueries.Where(query => query.UserId == admin.Id).ToListAsync());
    }

    [Fact]
    public async Task Logout_removes_the_session_when_realtime_throws()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var session = await world.App.LoginAsync(admin.EmailAddress!, "secret123456", null, null);
        world.Realtime.ThrowOnSignOut = true;
        await world.App.LogoutAsync(session!.Token, "https://fcm.googleapis.com/fcm/send/abc");
        Assert.Null(await world.Db.Sessions.FindAsync(session.Id));
        Assert.Equal(1, world.Realtime.SignOuts);
    }

    [Fact]
    public async Task A_message_survives_reopening_the_store()
    {
        var path = Path.Combine(Path.GetTempPath(), $"campfire-reopen-{Guid.NewGuid():n}.sqlite");
        long messageId;
        await using (var db = new CampfireDb(new DbContextOptionsBuilder<CampfireDb>().UseSqlite(Seeder.Connection(path)).Options))
        {
            await db.Database.EnsureCreatedAsync();
            var app = new CampfireApp(db, TimeProvider.System, new NoopRealtime());
            var admin = await app.FirstRunAsync("David", "david@37signals.com", "secret123456");
            var room = await db.Rooms.SingleAsync();
            var message = await app.CreateMessageAsync(admin.Id, room.Id, "kept across restart", null);
            messageId = message.Id;
        }

        SqliteConnection.ClearAllPools();
        await using (var db = new CampfireDb(new DbContextOptionsBuilder<CampfireDb>().UseSqlite(Seeder.Connection(path)).Options))
        {
            var message = await db.Messages.SingleAsync();
            Assert.Equal(messageId, message.Id);
            Assert.Equal("kept across restart", message.PlainText);
        }

        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public async Task Room_creation_can_be_limited_to_administrators()
    {
        using var world = new AppWorld();
        var admin = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var jason = await world.App.CreateMemberAsync("Jason", "jason@37signals.com", "secret123456");
        await world.App.SetRoomCreationPolicyAsync(admin.Id, true);
        await Assert.ThrowsAsync<AppException>(() => world.App.CreateRoomAsync(jason.Id, "Nope", RoomKind.Closed, [jason.Id]));
        var room = await world.App.CreateRoomAsync(admin.Id, "Yes", RoomKind.Closed, [admin.Id]);
        Assert.Equal("Yes", room.Name);
        var previous = (await world.App.AccountAsync()).JoinCode;
        var updated = await world.App.ResetJoinCodeAsync(admin.Id);
        Assert.NotEqual(previous, updated.JoinCode);
    }

    [Fact]
    public void Push_endpoints_follow_the_host_allow_list()
    {
        CampfireApp.ValidatePushEndpoint("https://updates.push.services.mozilla.com/wpush/v2/abc", _ => [IPAddress.Parse("93.184.216.34")]);
        CampfireApp.ValidatePushEndpoint("https://sub.fcm.googleapis.com/fcm/send/abc", _ => [IPAddress.Parse("8.8.8.8")]);
        Assert.Throws<AppException>(() => CampfireApp.ValidatePushEndpoint("https://notfcm.googleapis.com/fcm/send/abc", _ => [IPAddress.Parse("8.8.8.8")]));
        Assert.Throws<AppException>(() => CampfireApp.ValidatePushEndpoint("http://fcm.googleapis.com/fcm/send/abc", _ => [IPAddress.Parse("8.8.8.8")]));
        Assert.Throws<AppException>(() => CampfireApp.ValidatePushEndpoint("https://fcm.googleapis.com:8443/fcm/send/abc", _ => [IPAddress.Parse("8.8.8.8")]));
        Assert.Throws<AppException>(() => CampfireApp.ValidatePushEndpoint("https://fcm.googleapis.com/fcm/send/abc", _ => [IPAddress.Parse("192.168.1.1")]));
    }
}
