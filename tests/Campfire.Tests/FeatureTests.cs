using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Campfire.Core;
using Microsoft.EntityFrameworkCore;

namespace Campfire.Tests;

public sealed class FeatureTests
{
    [Fact]
    public async Task Closed_rooms_can_be_revised_opened_and_deleted_by_an_administrator_or_the_creator()
    {
        using var world = new FeatureWorld();
        var david = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var jason = await world.App.CreateMemberAsync("Jason", "jason@37signals.com", "secret123456");
        var kevin = await world.App.CreateMemberAsync("Kevin", "kevin@37signals.com", "secret123456");
        var room = await world.App.CreateRoomAsync(david.Id, "Design", RoomKind.Closed, [david.Id, jason.Id]);

        await world.App.SaveRoomAsync(david.Id, room.Id, RoomKind.Closed, "Design critique", [kevin.Id]);
        Assert.Equal("Design critique", (await world.Db.Rooms.SingleAsync(item => item.Id == room.Id)).Name);
        Assert.False(await world.App.IsMemberAsync(jason.Id, room.Id));
        Assert.True(await world.App.IsMemberAsync(kevin.Id, room.Id));
        Assert.True(await world.App.IsMemberAsync(david.Id, room.Id));

        var denied = await Assert.ThrowsAsync<AppException>(() => world.App.SaveRoomAsync(jason.Id, room.Id, RoomKind.Open, "Nope", null));
        Assert.Equal(403, denied.Status);
        var forbidden = await Assert.ThrowsAsync<AppException>(() => world.App.SaveRoomAsync(kevin.Id, room.Id, RoomKind.Closed, "Nope", [kevin.Id]));
        Assert.Equal(403, forbidden.Status);

        await world.App.SaveRoomAsync(david.Id, room.Id, RoomKind.Open, "Design critique", null);
        Assert.True(await world.App.IsMemberAsync(jason.Id, room.Id));

        var direct = await world.App.FindOrCreateDirectAsync(david.Id, [jason.Id]);
        var stuck = await Assert.ThrowsAsync<AppException>(() => world.App.SaveRoomAsync(david.Id, direct.Id, RoomKind.Open, "Direct", null));
        Assert.Equal(422, stuck.Status);
        var outsider = await Assert.ThrowsAsync<AppException>(() => world.App.DeleteRoomAsync(kevin.Id, direct.Id));
        Assert.Equal(404, outsider.Status);

        await world.App.DeleteRoomAsync(david.Id, room.Id);
        Assert.False(await world.Db.Rooms.AnyAsync(item => item.Id == room.Id));
        Assert.False(await world.Db.Memberships.AnyAsync(item => item.RoomId == room.Id));
    }

    [Fact]
    public void Notification_levels_cycle_and_directs_only_toggle_everything()
    {
        Assert.Equal(Involvement.Everything, CampfireApp.NextInvolvement(RoomKind.Open, Involvement.Mentions));
        Assert.Equal(Involvement.Nothing, CampfireApp.NextInvolvement(RoomKind.Open, Involvement.Everything));
        Assert.Equal(Involvement.Invisible, CampfireApp.NextInvolvement(RoomKind.Closed, Involvement.Nothing));
        Assert.Equal(Involvement.Mentions, CampfireApp.NextInvolvement(RoomKind.Open, Involvement.Invisible));
        Assert.Equal(Involvement.Nothing, CampfireApp.NextInvolvement(RoomKind.Direct, Involvement.Everything));
        Assert.Equal(Involvement.Everything, CampfireApp.NextInvolvement(RoomKind.Direct, Involvement.Nothing));
    }

    [Fact]
    public async Task A_permalink_loads_the_message_with_a_page_on_either_side()
    {
        using var world = new FeatureWorld();
        var david = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var room = await world.Db.Rooms.SingleAsync();
        var ids = new List<long>();
        for (var i = 0; i < 50; i++)
            ids.Add((await world.App.CreateMessageAsync(david.Id, room.Id, "m" + i, null)).Id);

        var page = await world.App.MessagesAroundAsync(room.Id, ids[44]);
        Assert.Equal(46, page.Messages.Count);
        Assert.Equal(ids[4], page.Messages[0].Id);
        Assert.Equal(ids[44], page.Messages[40].Id);
        Assert.Equal(ids[49], page.Messages[^1].Id);
        Assert.True(await world.App.HasOlderAsync(page.Messages));
        Assert.False(await world.App.HasNewerAsync(page.Messages));
    }

    [Fact]
    public async Task Attachments_are_stored_once_and_removed_with_the_message()
    {
        using var world = new FeatureWorld();
        var david = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var room = await world.Db.Rooms.SingleAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var message = await world.App.CreateMessageAsync(david.Id, room.Id, "pic", null, new IncomingFile("dot.png", "text/plain", png), true);
        var stored = await world.Db.Attachments.SingleAsync();
        Assert.Equal("image/png", stored.ContentType);
        Assert.Equal(1, stored.Width);
        Assert.Equal(1, stored.Height);
        Assert.Equal(png, await world.App.ReadAttachmentAsync(message.Id));
        Assert.Contains("lightbox-link", Presentation.Body(message));

        var notes = await world.App.CreateMessageAsync(david.Id, room.Id, "", null, new IncomingFile("notes.txt", "text/plain", "hello"u8.ToArray()), true);
        Assert.Equal("notes.txt", notes.PlainText);
        Assert.Contains("download=1", Presentation.Body(notes));

        var sound = await world.App.CreateMessageAsync(david.Id, room.Id, "/play bell", null);
        Assert.Contains("sound__audio", world.Realtime.Messages[^1].Html);
        Assert.Contains("/assets/sounds/bell.mp3", Presentation.Body(sound));

        var key = stored.StorageKey;
        await world.App.DeleteMessageAsync(david.Id, room.Id, message.Id);
        Assert.Null(await world.App.ReadAttachmentAsync(message.Id));
        Assert.Null(world.Files.Read(key));
        await Assert.ThrowsAsync<AppException>(() => world.App.CreateMessageAsync(david.Id, room.Id, "", null, new IncomingFile("empty.txt", "text/plain", []), true));
    }

    [Fact]
    public async Task Webhooks_and_pushes_follow_room_kind_mentions_and_presence()
    {
        using var world = new FeatureWorld();
        var david = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var jason = await world.App.CreateMemberAsync("Jason", "jason@37signals.com", "secret123456");
        var bot = await world.App.CreateBotAsync(david.Id, "Helper", "https://example.com/hook");
        var room = await world.Db.Rooms.SingleAsync(item => item.Kind == RoomKind.Open);
        await world.App.AddPushSubscriptionAsync(jason.Id, "https://fcm.googleapis.com/fcm/send/device", "key", "auth", "test", _ => [IPAddress.Parse("93.184.216.34")]);

        await world.App.CreateMessageAsync(david.Id, room.Id, "hello", null);
        Assert.Empty(world.Outbound.Webhooks);
        Assert.Empty(world.Outbound.Pushes);

        world.Outbound.Pushes.Clear();
        var mentioned = await world.App.CreateMessageAsync(david.Id, room.Id, "@Jason @Helper please", null);
        Assert.Single(world.Outbound.Webhooks);
        var call = world.Outbound.Webhooks[0];
        Assert.Equal(bot.Id, call.BotId);
        Assert.Equal("https://example.com/hook", call.Url);
        using var json = JsonDocument.Parse(call.Json);
        Assert.Equal(david.Id, json.RootElement.GetProperty("user").GetProperty("id").GetInt64());
        Assert.Equal("/rooms/" + room.Id + "/" + CampfireApp.BotKey(bot) + "/messages", json.RootElement.GetProperty("room").GetProperty("path").GetString());
        Assert.DoesNotContain("@Helper", json.RootElement.GetProperty("message").GetProperty("body").GetProperty("plain").GetString());
        Assert.Equal("/rooms/" + room.Id + "/@" + mentioned.Id, json.RootElement.GetProperty("message").GetProperty("path").GetString());
        var push = Assert.Single(world.Outbound.Pushes);
        Assert.Equal("All Talk", push.Title);
        Assert.StartsWith("David: ", push.Body);
        Assert.Equal("/rooms/" + room.Id + "/@" + mentioned.Id, push.Path);
        Assert.True(push.Badge >= 1);

        world.Outbound.Webhooks.Clear();
        world.Outbound.Pushes.Clear();
        await world.App.ConnectAsync(jason.Id, room.Id);
        await world.App.CreateMessageAsync(david.Id, room.Id, "@Jason still here", null);
        Assert.Empty(world.Outbound.Pushes);

        await world.App.DisconnectAsync(jason.Id, room.Id);
        await world.App.SetInvolvementAsync(jason.Id, room.Id, Involvement.Nothing);
        world.Outbound.Pushes.Clear();
        await world.App.CreateMessageAsync(david.Id, room.Id, "@Jason quiet", null);
        Assert.Empty(world.Outbound.Pushes);

        await world.App.SetInvolvementAsync(jason.Id, room.Id, Involvement.Everything);
        world.Outbound.Pushes.Clear();
        await world.App.CreateMessageAsync(david.Id, room.Id, "everyone hears this", null);
        Assert.Single(world.Outbound.Pushes);

        world.Outbound.Webhooks.Clear();
        var direct = await world.App.FindOrCreateDirectAsync(david.Id, [bot.Id]);
        await world.App.CreateMessageAsync(david.Id, direct.Id, "ping", null);
        Assert.Equal(bot.Id, Assert.Single(world.Outbound.Webhooks).BotId);

        world.Outbound.Webhooks.Clear();
        await world.App.CreateMessageAsync(david.Id, room.Id, "@Helper silent", null, null, false);
        Assert.Empty(world.Outbound.Webhooks);

        await world.App.UpdateBotAsync(david.Id, bot.Id, "Helper", null);
        Assert.Null((await world.Db.Users.Include(user => user.Webhook).SingleAsync(user => user.Id == bot.Id)).Webhook);
        await world.App.DeactivateBotAsync(david.Id, bot.Id);
        Assert.Empty(await world.App.BotsAsync());
    }

    [Fact]
    public async Task Autocomplete_returns_active_people_in_the_room()
    {
        using var world = new FeatureWorld();
        var david = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var jason = await world.App.CreateMemberAsync("Jason", "jason@37signals.com", "secret123456");
        await world.App.CreateBotAsync(david.Id, "Jason Bot", null);
        var closed = await world.App.CreateRoomAsync(david.Id, "Design", RoomKind.Closed, [david.Id]);

        var matches = await world.App.AutocompleteAsync(null, "jas");
        Assert.Equal(jason.Id, Assert.Single(matches).Id);
        var inRoom = await world.App.AutocompleteAsync(closed.Id, null);
        Assert.Equal(david.Id, Assert.Single(inRoom).Id);
    }

    [Fact]
    public void Transfer_links_expire_after_four_hours_and_reject_a_bad_signature()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var issued = Transfers.Issue(7, DateTimeOffset.UnixEpoch.AddHours(4), key);
        Assert.Equal(7, Transfers.Read(issued, key, DateTimeOffset.UnixEpoch.AddHours(4)));
        Assert.Null(Transfers.Read(issued, key, DateTimeOffset.UnixEpoch.AddHours(4).AddSeconds(1)));
        Assert.Null(Transfers.Read(issued + "x", key, DateTimeOffset.UnixEpoch));
        Assert.Null(Transfers.Read(issued, RandomNumberGenerator.GetBytes(32), DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task A_signed_transfer_signs_the_matching_active_user_back_in()
    {
        using var world = new FeatureWorld();
        var david = await world.App.FirstRunAsync("David", "david@37signals.com", "secret123456");
        var token = world.App.IssueTransfer(david.Id);
        var user = await world.App.RedeemTransferAsync(token);
        Assert.Equal(david.Id, user?.Id);
        world.Time.Advance(Transfers.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Null(await world.App.RedeemTransferAsync(token));
    }

    [Fact]
    public void Qr_codes_keep_the_finder_patterns_and_round_trip_the_payload()
    {
        const string url = "https://campfire.example/join/ABCD";
        Assert.Equal(url, QrCode.Text(QrCode.Token(url)));
        var modules = QrCode.Encode(Encoding.UTF8.GetBytes(url));
        var size = modules.GetLength(0);
        Assert.Equal(size, modules.GetLength(1));
        AssertFinder(modules, 0, 0);
        AssertFinder(modules, size - 7, 0);
        AssertFinder(modules, 0, size - 7);
        Assert.True(modules[size - 8, 8]);
        var svg = QrCode.Svg(url);
        Assert.Contains("<svg", svg);
        Assert.Contains("M0 0h1v1h-1z", svg);
    }

    [Fact]
    public void Web_push_round_trips_the_payload_and_signs_a_vapid_token()
    {
        using var client = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var parameters = client.ExportParameters(true);
        var point = Uncompressed(parameters);
        var auth = RandomNumberGenerator.GetBytes(16);
        var plaintext = Encoding.UTF8.GetBytes(WebPush.Payload("All Talk", "David: hello", "/rooms/1/@2", 3));
        var body = WebPush.Encrypt(plaintext, Base64Url.Encode(point), Base64Url.Encode(auth), out _, out _);
        var decrypted = WebPush.Decrypt(body, parameters.D!, point, auth);
        Assert.Equal(plaintext, decrypted);

        var (publicKey, privateKey) = WebPush.GenerateVapid();
        var token = WebPush.Sign("https://fcm.googleapis.com", "mailto:support@37signals.com", DateTimeOffset.UnixEpoch.AddHours(12), publicKey, privateKey);
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Base64Url.Decode(publicKey)[1..33], Y = Base64Url.Decode(publicKey)[33..65] },
        });
        var input = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        Assert.True(key.VerifyData(input, Base64Url.Decode(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        using var claims = JsonDocument.Parse(Base64Url.Decode(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:support@37signals.com", claims.RootElement.GetProperty("sub").GetString());
    }

    [Fact]
    public void A_preview_is_publishable_only_when_its_title_description_and_hosts_are_public()
    {
        static IReadOnlyList<IPAddress> Dns(string host) => host == "secret.example"
            ? [IPAddress.Parse("10.0.0.1")]
            : [IPAddress.Parse("93.184.216.34")];

        var complete = new LinkPreview("https://example.com/page", "Title", "Description", null);
        Assert.True(Unfurl.Publishable(complete, Dns));
        Assert.False(Unfurl.Publishable(complete with { Description = "" }, Dns));
        Assert.False(Unfurl.Publishable(complete with { ImageUrl = "https://secret.example/a.png" }, Dns));
        var sound = Sound.Find("bell");
        Assert.NotNull(sound);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("🔔"), sound.Html());
        Assert.Null(Sound.FromPlain("hello"));
        Assert.Equal("nyan", Sound.FromPlain("/play nyan")?.Name);
    }

    private static void AssertFinder(bool[,] modules, int originX, int originY)
    {
        var rows = new[] { "1111111", "1000001", "1011101", "1011101", "1011101", "1000001", "1111111" };
        for (var y = 0; y < 7; y++)
            for (var x = 0; x < 7; x++)
                Assert.Equal(rows[y][x] == '1', modules[originY + y, originX + x]);
    }

    private static byte[] Uncompressed(ECParameters parameters)
    {
        var point = new byte[65];
        point[0] = 4;
        Pad(parameters.Q.X!).CopyTo(point, 1);
        Pad(parameters.Q.Y!).CopyTo(point, 33);
        return point;
    }

    private static byte[] Pad(byte[] coordinate)
    {
        var padded = new byte[32];
        coordinate.AsSpan(Math.Max(0, coordinate.Length - 32)).CopyTo(padded.AsSpan(32 - Math.Min(32, coordinate.Length)));
        return padded;
    }

    private sealed class FeatureWorld : IDisposable
    {
        public FeatureWorld()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"campfire-feature-{Guid.NewGuid():n}");
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "campfire.sqlite");
            var options = new DbContextOptionsBuilder<CampfireDb>().UseSqlite(Seeder.Connection(DatabasePath)).Options;
            Db = new CampfireDb(options);
            Db.Database.EnsureCreated();
            Time = new ManualTime();
            Realtime = new RecordingRealtime();
            Files = new FileCabinet(Path.Combine(directory, "files"));
            Outbound = new RecordingOutbound();
            var secrets = new AppSecrets(RandomNumberGenerator.GetBytes(32), null, null);
            App = new CampfireApp(Db, Time, Realtime, Files, Outbound, secrets);
        }

        public string DatabasePath { get; }
        public CampfireDb Db { get; }
        public ManualTime Time { get; }
        public RecordingRealtime Realtime { get; }
        public FileCabinet Files { get; }
        public RecordingOutbound Outbound { get; }
        public CampfireApp App { get; }

        public void Dispose()
        {
            Db.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var directory = Path.GetDirectoryName(DatabasePath);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
