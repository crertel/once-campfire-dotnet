using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace Campfire.Core;

public sealed record MessagePage(bool RoomHasMessages, IReadOnlyList<Message> Messages);

public sealed class CampfireApp
{
    public const int PageSize = 40;
    public static readonly TimeSpan ConnectionTtl = TimeSpan.FromSeconds(60);

    private readonly CampfireDb _db;
    private readonly TimeProvider _time;
    private readonly ICampfireRealtime _realtime;

    public CampfireApp(CampfireDb db, TimeProvider time, ICampfireRealtime realtime)
    {
        _db = db;
        _time = time;
        _realtime = realtime;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<bool> HasAccountAsync(CancellationToken cancellationToken = default) =>
        await _db.Accounts.AnyAsync(cancellationToken);

    public async Task<Account> AccountAsync(CancellationToken cancellationToken = default) =>
        await _db.Accounts.SingleAsync(cancellationToken);

    public async Task<User> FirstRunAsync(string name, string email, string password, CancellationToken cancellationToken = default)
    {
        if (await _db.Accounts.AnyAsync(cancellationToken))
            throw new AppException(409, "Campfire is already set up.");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new AppException(422, "Name, email, and password are required.");

        var now = Now;
        _db.Accounts.Add(new Account
        {
            Name = "Campfire",
            JoinCode = NewJoinCode(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        var admin = NewUser(name.Trim(), email.Trim(), password, UserRole.Administrator, now);
        _db.Users.Add(admin);
        await _db.SaveChangesAsync(cancellationToken);

        await CreateRoomAsync(admin.Id, "All Talk", RoomKind.Open, null, cancellationToken);
        return admin;
    }

    public async Task<User> JoinAsync(string joinCode, string name, string email, string password, CancellationToken cancellationToken = default)
    {
        var account = await _db.Accounts.SingleOrDefaultAsync(cancellationToken) ?? throw new AppException(404, "Campfire is not set up.");
        if (!string.Equals(account.JoinCode, joinCode, StringComparison.Ordinal))
            throw new AppException(404, "That join code is not valid.");
        return await CreateMemberAsync(name, email, password, cancellationToken);
    }

    public async Task<User> CreateMemberAsync(string name, string email, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new AppException(422, "Name, email, and password are required.");
        var normalized = email.Trim();
        if (await _db.Users.AnyAsync(user => user.EmailAddress == normalized, cancellationToken))
            throw new AppException(422, "Email has already been taken.");

        var member = NewUser(name.Trim(), normalized, password, UserRole.Member, Now);
        _db.Users.Add(member);
        await _db.SaveChangesAsync(cancellationToken);
        await GrantOpenRoomsAsync(member, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return member;
    }

    public async Task<User> UpdateProfileAsync(long userId, string? name, string? email, string? bio, string? password, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(name))
            user.Name = name.Trim();
        if (bio is not null)
            user.Bio = bio;
        if (!string.IsNullOrWhiteSpace(email) && !string.Equals(email.Trim(), user.EmailAddress, StringComparison.Ordinal))
        {
            var normalized = email.Trim();
            if (await _db.Users.AnyAsync(other => other.EmailAddress == normalized && other.Id != user.Id, cancellationToken))
                throw new AppException(422, "Email has already been taken.");
            user.EmailAddress = normalized;
        }
        if (!string.IsNullOrEmpty(password))
            user.PasswordHash = Passwords.Hash(password);
        user.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task DeactivateAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        var drop = await _db.Memberships.Include(membership => membership.Room)
            .Where(membership => membership.UserId == user.Id && membership.Room.Kind != RoomKind.Direct)
            .ToListAsync(cancellationToken);
        _db.Memberships.RemoveRange(drop);
        var pushes = await _db.PushSubscriptions.Where(subscription => subscription.UserId == user.Id).ToListAsync(cancellationToken);
        _db.PushSubscriptions.RemoveRange(pushes);
        var searches = await _db.SearchQueries.Where(search => search.UserId == user.Id).ToListAsync(cancellationToken);
        _db.SearchQueries.RemoveRange(searches);
        var sessions = await _db.Sessions.Where(session => session.UserId == user.Id).ToListAsync(cancellationToken);
        _db.Sessions.RemoveRange(sessions);
        if (!string.IsNullOrEmpty(user.EmailAddress))
            user.EmailAddress = user.EmailAddress.Replace("@", $"-deactivated-{Guid.NewGuid()}@", StringComparison.Ordinal);
        user.Status = UserStatus.Deactivated;
        user.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        await QuietAsync(() => _realtime.SignedOutAsync(user.Id, cancellationToken));
    }

    public async Task BanAsync(long actorId, long userId, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can ban a member.");
        var user = await RequireUserAsync(userId, cancellationToken);
        var ips = await _db.Sessions.Where(session => session.UserId == user.Id && session.IpAddress != null)
            .Select(session => session.IpAddress!)
            .Distinct()
            .ToListAsync(cancellationToken);
        var now = Now;
        foreach (var ip in ips)
            _db.Bans.Add(new Ban { UserId = user.Id, IpAddress = ip, CreatedAt = now });
        var sessions = await _db.Sessions.Where(session => session.UserId == user.Id).ToListAsync(cancellationToken);
        _db.Sessions.RemoveRange(sessions);
        user.Status = UserStatus.Banned;
        user.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
        await QuietAsync(() => _realtime.SignedOutAsync(user.Id, cancellationToken));

        var messages = await _db.Messages.Where(message => message.CreatorId == user.Id).ToListAsync(cancellationToken);
        foreach (var message in messages)
            await DeleteMessageRowAsync(message, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UnbanAsync(long actorId, long userId, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can unban a member.");
        var user = await RequireUserAsync(userId, cancellationToken);
        var bans = await _db.Bans.Where(ban => ban.UserId == user.Id).ToListAsync(cancellationToken);
        _db.Bans.RemoveRange(bans);
        user.Status = UserStatus.Active;
        user.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<User> CreateBotAsync(long actorId, string name, string? webhookUrl, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can create a bot.");
        if (string.IsNullOrWhiteSpace(name))
            throw new AppException(422, "Name is required.");

        var bot = NewUser(name.Trim(), null, null, UserRole.Bot, Now);
        bot.PasswordHash = null;
        bot.BotToken = Alphanumeric(12);
        _db.Users.Add(bot);
        await _db.SaveChangesAsync(cancellationToken);
        await GrantOpenRoomsAsync(bot, cancellationToken);
        if (!string.IsNullOrWhiteSpace(webhookUrl))
            _db.Webhooks.Add(new Webhook { UserId = bot.Id, Url = webhookUrl });
        await _db.SaveChangesAsync(cancellationToken);
        return bot;
    }

    public static string BotKey(User bot) => $"{bot.Id}-{bot.BotToken}";

    public async Task<User> ResetBotKeyAsync(long actorId, long botId, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can reset a bot key.");
        var bot = await _db.Users.FirstAsync(user => user.Id == botId && user.Role == UserRole.Bot, cancellationToken);
        bot.BotToken = Alphanumeric(12);
        bot.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        return bot;
    }

    public async Task<User?> AuthenticateBotAsync(string botKey, CancellationToken cancellationToken = default)
    {
        var parts = botKey.Split('-');
        if (parts.Length < 2 || !long.TryParse(parts[0], out var id))
            return null;
        var token = parts[1];
        return await _db.Users.FirstOrDefaultAsync(
            user => user.Id == id && user.BotToken == token && user.Role == UserRole.Bot && user.Status == UserStatus.Active,
            cancellationToken);
    }

    public async Task<Room> CreateRoomAsync(long creatorId, string name, RoomKind kind, IReadOnlyCollection<long>? memberIds, CancellationToken cancellationToken = default)
    {
        if (kind == RoomKind.Direct)
            throw new AppException(422, "Direct rooms are created from their members.");
        var creator = await RequireUserAsync(creatorId, cancellationToken);
        var account = await _db.Accounts.SingleAsync(cancellationToken);
        if (account.RestrictRoomCreationToAdministrators && !creator.IsAdministrator)
            throw new AppException(403, "Only administrators can create rooms.");
        if (string.IsNullOrWhiteSpace(name))
            throw new AppException(422, "Name is required.");

        var now = Now;
        var room = new Room
        {
            Name = name.Trim(),
            Kind = kind,
            CreatorId = creator.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Rooms.Add(room);
        await _db.SaveChangesAsync(cancellationToken);

        if (kind == RoomKind.Open)
        {
            var active = await _db.Users.Where(user => user.Status == UserStatus.Active).ToListAsync(cancellationToken);
            await GrantAsync(room, active, cancellationToken);
        }
        else
        {
            var ids = (memberIds ?? []).Append(creator.Id).Distinct().ToArray();
            var users = await _db.Users.Where(user => ids.Contains(user.Id)).ToListAsync(cancellationToken);
            await GrantAsync(room, users, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return room;
    }

    public async Task<Room> ChangeRoomKindAsync(long actorId, long roomId, RoomKind kind, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        var room = await _db.Rooms.FirstAsync(item => item.Id == roomId, cancellationToken);
        if (!actor.IsAdministrator && actor.Id != room.CreatorId)
            throw new AppException(403, "You cannot change this room.");
        if (room.Kind == RoomKind.Direct && kind != RoomKind.Direct)
            throw new AppException(422, "can't be changed for a direct room");

        var opening = room.Kind != RoomKind.Open && kind == RoomKind.Open;
        room.Kind = kind;
        room.UpdatedAt = Now;
        if (opening)
        {
            var active = await _db.Users.Where(user => user.Status == UserStatus.Active).ToListAsync(cancellationToken);
            await GrantAsync(room, active, cancellationToken);
        }
        await _db.SaveChangesAsync(cancellationToken);
        return room;
    }

    public async Task<Room> FindOrCreateDirectAsync(long creatorId, IReadOnlyCollection<long> userIds, CancellationToken cancellationToken = default)
    {
        var creator = await RequireUserAsync(creatorId, cancellationToken);
        var ids = userIds.Append(creator.Id).Distinct().Order().ToArray();
        if (ids.Length < 2)
            throw new AppException(422, "A direct room needs two people.");
        var key = string.Join(',', ids);
        var existing = await _db.Rooms.FirstOrDefaultAsync(room => room.DirectKey == key, cancellationToken);
        if (existing is not null)
            return existing;

        var now = Now;
        var room = new Room
        {
            Kind = RoomKind.Direct,
            CreatorId = creator.Id,
            DirectKey = key,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Rooms.Add(room);
        await _db.SaveChangesAsync(cancellationToken);
        var users = await _db.Users.Where(user => ids.Contains(user.Id)).ToListAsync(cancellationToken);
        await GrantAsync(room, users, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return room;
    }

    public async Task SetInvolvementAsync(long userId, long roomId, Involvement involvement, CancellationToken cancellationToken = default)
    {
        var membership = await RequireMembershipAsync(userId, roomId, cancellationToken);
        membership.Involvement = involvement;
        membership.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ConnectAsync(long userId, long roomId, CancellationToken cancellationToken = default)
    {
        var membership = await RequireMembershipAsync(userId, roomId, cancellationToken);
        var now = Now;
        membership.Connections = IsConnected(membership, now) ? membership.Connections + 1 : 1;
        membership.ConnectedAt = now;
        membership.UnreadAt = null;
        membership.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DisconnectAsync(long userId, long roomId, CancellationToken cancellationToken = default)
    {
        var membership = await _db.Memberships.FirstOrDefaultAsync(item => item.UserId == userId && item.RoomId == roomId, cancellationToken);
        if (membership is null)
            return;
        var now = Now;
        membership.Connections = IsConnected(membership, now) ? membership.Connections - 1 : 0;
        if (membership.Connections < 1)
        {
            membership.Connections = 0;
            membership.ConnectedAt = null;
        }
        membership.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RefreshConnectionAsync(long userId, long roomId, CancellationToken cancellationToken = default)
    {
        var membership = await RequireMembershipAsync(userId, roomId, cancellationToken);
        var now = Now;
        if (!IsConnected(membership, now))
            membership.Connections = 1;
        membership.ConnectedAt = now;
        membership.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public static bool IsConnected(Membership membership, DateTime now) =>
        membership.ConnectedAt is not null && membership.ConnectedAt.Value >= now - ConnectionTtl;

    public async Task<Message> CreateMessageAsync(long userId, long roomId, string body, string? clientMessageId, CancellationToken cancellationToken = default)
    {
        await RequireMembershipAsync(userId, roomId, cancellationToken);
        if (!string.IsNullOrEmpty(clientMessageId))
        {
            var prior = await _db.Messages.FirstOrDefaultAsync(
                message => message.RoomId == roomId && message.ClientMessageId == clientMessageId,
                cancellationToken);
            if (prior is not null)
                return prior;
        }

        var members = await MemberUsersAsync(roomId, cancellationToken);
        var creator = members.First(user => user.Id == userId);
        var html = HtmlText.Compose(body, members, null);
        var now = Now;
        var message = new Message
        {
            RoomId = roomId,
            CreatorId = userId,
            ClientMessageId = string.IsNullOrEmpty(clientMessageId) ? Guid.NewGuid().ToString("n") : clientMessageId,
            Html = html,
            PlainText = HtmlText.ToPlain(html),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Messages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);
        await ReindexAsync(message, cancellationToken);
        await StampUnreadAsync(message, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await BroadcastMessageAsync(message, creator, cancellationToken);
        return message;
    }

    public async Task<Message> UpdateMessageAsync(long userId, long roomId, long messageId, string body, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        var message = await RequireMessageAsync(roomId, messageId, cancellationToken);
        if (!CanAdminister(user, message))
            throw new AppException(403, "You cannot change this message.");
        var members = await MemberUsersAsync(roomId, cancellationToken);
        message.Html = HtmlText.Compose(body, members, null);
        message.PlainText = HtmlText.ToPlain(message.Html);
        message.UpdatedAt = Now;
        await ReindexAsync(message, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await _realtime.MessageAsync(Live(message, user), cancellationToken);
        return message;
    }

    public async Task DeleteMessageAsync(long userId, long roomId, long messageId, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        var message = await RequireMessageAsync(roomId, messageId, cancellationToken);
        if (!CanAdminister(user, message))
            throw new AppException(403, "You cannot change this message.");
        await DeleteMessageRowAsync(message, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public static bool CanAdminister(User user, Message message) =>
        user.IsAdministrator || user.Id == message.CreatorId;

    public async Task<MessagePage> MessagesPageAsync(long roomId, long? beforeId, long? afterId, CancellationToken cancellationToken = default)
    {
        var any = await _db.Messages.AnyAsync(message => message.RoomId == roomId, cancellationToken);
        if (!any)
            return new MessagePage(false, []);

        List<Message> page;
        if (beforeId is not null)
        {
            var cursor = await _db.Messages.FirstOrDefaultAsync(message => message.Id == beforeId && message.RoomId == roomId, cancellationToken);
            if (cursor is null)
                page = [];
            else
            {
                page = await BeforeQuery(roomId, cursor).Include(message => message.Creator).Include(message => message.Boosts).ToListAsync(cancellationToken);
                page.Reverse();
            }
        }
        else if (afterId is not null)
        {
            var cursor = await _db.Messages.FirstOrDefaultAsync(message => message.Id == afterId && message.RoomId == roomId, cancellationToken);
            if (cursor is null)
                page = [];
            else
                page = await _db.Messages.Where(message => message.RoomId == roomId && (message.CreatedAt > cursor.CreatedAt || (message.CreatedAt == cursor.CreatedAt && message.Id > cursor.Id)))
                    .OrderBy(message => message.CreatedAt).ThenBy(message => message.Id)
                    .Take(PageSize)
                    .Include(message => message.Creator)
                    .Include(message => message.Boosts)
                    .ToListAsync(cancellationToken);
        }
        else
        {
            page = await LastPageQuery(roomId).Include(message => message.Creator).Include(message => message.Boosts).ToListAsync(cancellationToken);
            page.Reverse();
        }

        return new MessagePage(true, page);
    }

    public List<Message> ReadLastPage(long roomId)
    {
        var page = LastPageQuery(roomId).Include(message => message.Creator).ToList();
        page.Reverse();
        return page;
    }

    public List<Message> ReadBefore(long roomId, long beforeId)
    {
        var cursor = _db.Messages.First(message => message.Id == beforeId && message.RoomId == roomId);
        var page = BeforeQuery(roomId, cursor).Include(message => message.Creator).ToList();
        page.Reverse();
        return page;
    }

    public List<Membership> ReadSidebar(long userId) =>
        _db.Memberships.Where(membership => membership.UserId == userId).Include(membership => membership.Room).ToList();

    public List<Message> ReadSearch(long userId, string query) => FindMessages(userId, query);

    public async Task<Boost> CreateBoostAsync(long userId, long roomId, long messageId, string content, CancellationToken cancellationToken = default)
    {
        await RequireMembershipAsync(userId, roomId, cancellationToken);
        if (string.IsNullOrEmpty(content))
            throw new AppException(422, "can't be blank");
        if (content.Length > 16)
            throw new AppException(422, "is too long (maximum is 16 characters)");
        var message = await RequireMessageAsync(roomId, messageId, cancellationToken);
        var existing = await _db.Boosts.FirstOrDefaultAsync(boost => boost.MessageId == message.Id && boost.BoosterId == userId, cancellationToken);
        if (existing is not null)
        {
            existing.Content = content;
            await _db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var boost = new Boost
        {
            MessageId = message.Id,
            BoosterId = userId,
            Content = content,
            CreatedAt = Now,
        };
        _db.Boosts.Add(boost);
        await _db.SaveChangesAsync(cancellationToken);
        return boost;
    }

    public async Task DeleteBoostAsync(long userId, long roomId, long messageId, CancellationToken cancellationToken = default)
    {
        await RequireMessageAsync(roomId, messageId, cancellationToken);
        var boost = await _db.Boosts.FirstOrDefaultAsync(item => item.MessageId == messageId && item.BoosterId == userId, cancellationToken);
        if (boost is null)
            return;
        _db.Boosts.Remove(boost);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveDraftAsync(long userId, long roomId, string body, CancellationToken cancellationToken = default)
    {
        await RequireMembershipAsync(userId, roomId, cancellationToken);
        var draft = await _db.Drafts.FirstOrDefaultAsync(item => item.UserId == userId && item.RoomId == roomId, cancellationToken);
        if (draft is null)
            _db.Drafts.Add(new Draft { UserId = userId, RoomId = roomId, Body = body });
        else
            draft.Body = body;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> DraftAsync(long userId, long roomId, CancellationToken cancellationToken = default) =>
        await _db.Drafts.Where(draft => draft.UserId == userId && draft.RoomId == roomId).Select(draft => draft.Body).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Message>> SearchAsync(long userId, string query, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            _db.SearchQueries.Add(new SearchQuery { UserId = userId, Query = query.Trim(), CreatedAt = Now });
            await _db.SaveChangesAsync(cancellationToken);
        }
        return FindMessages(userId, query);
    }

    public async Task<Session?> LoginAsync(string email, string password, string? ip, string? userAgent, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(
            item => item.EmailAddress == email && item.Status == UserStatus.Active,
            cancellationToken);
        if (user is null || !Passwords.Verify(password, user.PasswordHash))
            return null;

        var now = Now;
        var session = new Session
        {
            Token = UrlToken(),
            UserId = user.Id,
            User = user,
            IpAddress = ip,
            UserAgent = userAgent,
            LastActiveAt = now,
            CreatedAt = now,
        };
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<User?> UserFromTokenAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        var session = await _db.Sessions.Include(item => item.User).FirstOrDefaultAsync(item => item.Token == token, cancellationToken);
        if (session is null || !session.User.IsActive)
            return null;
        return session.User;
    }

    public async Task LogoutAsync(string? token, string? pushEndpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
            return;
        var session = await _db.Sessions.FirstOrDefaultAsync(item => item.Token == token, cancellationToken);
        if (session is null)
            return;
        if (!string.IsNullOrEmpty(pushEndpoint))
        {
            var subscription = await _db.PushSubscriptions.FirstOrDefaultAsync(
                item => item.UserId == session.UserId && item.Endpoint == pushEndpoint,
                cancellationToken);
            if (subscription is not null)
                _db.PushSubscriptions.Remove(subscription);
        }

        var userId = session.UserId;
        _db.Sessions.Remove(session);
        await _db.SaveChangesAsync(cancellationToken);
        await QuietAsync(() => _realtime.SignedOutAsync(userId, cancellationToken));
    }

    public async Task<PushSubscription> AddPushSubscriptionAsync(
        long userId,
        string endpoint,
        string? p256dh,
        string? auth,
        string? userAgent,
        Func<string, IReadOnlyList<IPAddress>> dns,
        CancellationToken cancellationToken = default)
    {
        ValidatePushEndpoint(endpoint, dns);
        var subscription = new PushSubscription
        {
            UserId = userId,
            Endpoint = endpoint,
            P256dhKey = p256dh,
            AuthKey = auth,
            UserAgent = userAgent,
            CreatedAt = Now,
        };
        _db.PushSubscriptions.Add(subscription);
        await _db.SaveChangesAsync(cancellationToken);
        return subscription;
    }

    public static void ValidatePushEndpoint(string endpoint, Func<string, IReadOnlyList<IPAddress>> dns)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new AppException(422, "endpoint is not a valid URL");
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new AppException(422, "endpoint must use HTTPS");
        if (uri.Port != 443)
            throw new AppException(422, "endpoint must use the default HTTPS port");

        var host = uri.IdnHost.ToLowerInvariant();
        var permitted = PushHosts.Any(item => host == item || host.EndsWith("." + item, StringComparison.Ordinal));
        if (!permitted)
            throw new AppException(422, "endpoint is not a permitted push service");

        try
        {
            _ = PrivateNetwork.Resolve(host, dns);
        }
        catch (Exception exception) when (exception is ViolationException or UnresolvableException)
        {
            throw new AppException(422, "endpoint resolves to a private or invalid IP address");
        }
    }

    public static readonly string[] PushHosts =
    [
        "jmt17.google.com",
        "fcm.googleapis.com",
        "updates.push.services.mozilla.com",
        "web.push.apple.com",
        "notify.windows.com",
    ];

    public async Task<Account> ResetJoinCodeAsync(long actorId, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can reset the join code.");
        var account = await AccountAsync(cancellationToken);
        account.JoinCode = NewJoinCode();
        account.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        return account;
    }

    public async Task<Account> SetCustomStylesAsync(long actorId, string? styles, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can edit styles.");
        var account = await AccountAsync(cancellationToken);
        account.CustomStyles = styles?.Replace("<", "", StringComparison.Ordinal);
        account.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        return account;
    }

    public async Task<Account> SetRoomCreationPolicyAsync(long actorId, bool administratorsOnly, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can change that setting.");
        var account = await AccountAsync(cancellationToken);
        account.RestrictRoomCreationToAdministrators = administratorsOnly;
        account.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
        return account;
    }

    public async Task SetLogoAsync(long actorId, byte[] bytes, string contentType, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can change the logo.");
        var account = await AccountAsync(cancellationToken);
        account.Logo = bytes;
        account.LogoContentType = contentType;
        account.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearLogoAsync(long actorId, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can change the logo.");
        var account = await AccountAsync(cancellationToken);
        account.Logo = null;
        account.LogoContentType = null;
        account.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetAvatarAsync(long userId, byte[] bytes, string contentType, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        user.Avatar = bytes;
        user.AvatarContentType = contentType;
        user.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAvatarAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(userId, cancellationToken);
        user.Avatar = null;
        user.AvatarContentType = null;
        user.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Membership>> SidebarAsync(long userId, CancellationToken cancellationToken = default) =>
        await _db.Memberships.Where(membership => membership.UserId == userId)
            .Include(membership => membership.Room)
            .OrderBy(membership => membership.Room.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<User>> UsersAsync(CancellationToken cancellationToken = default) =>
        await _db.Users.OrderBy(user => user.Name).ToListAsync(cancellationToken);

    public async Task<Room?> OriginalRoomAsync(long userId, CancellationToken cancellationToken = default) =>
        await _db.Memberships.Where(membership => membership.UserId == userId)
            .OrderBy(membership => membership.CreatedAt)
            .Select(membership => membership.Room)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<bool> IsMemberAsync(long userId, long roomId, CancellationToken cancellationToken = default) =>
        await _db.Memberships.AnyAsync(membership => membership.UserId == userId && membership.RoomId == roomId, cancellationToken);

    public static IReadOnlyList<IPAddress> SystemDns(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    private List<Message> FindMessages(long userId, string query)
    {
        var terms = HtmlText.Terms(query).Distinct().ToArray();
        if (terms.Length == 0)
            return [];

        var roomIds = _db.Memberships.Where(membership => membership.UserId == userId).Select(membership => membership.RoomId).ToList();
        List<long>? hits = null;
        foreach (var term in terms)
        {
            var ids = _db.MessageTokens.Where(token => token.Term == term).Select(token => token.MessageId).ToList();
            hits = hits is null ? ids : hits.Intersect(ids).ToList();
        }

        var matched = hits ?? [];
        return _db.Messages.Where(message => matched.Contains(message.Id) && roomIds.Contains(message.RoomId))
            .Include(message => message.Creator)
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .ToList();
    }

    private IQueryable<Message> LastPageQuery(long roomId) =>
        _db.Messages.Where(message => message.RoomId == roomId)
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(PageSize);

    private IQueryable<Message> BeforeQuery(long roomId, Message cursor) =>
        _db.Messages.Where(message => message.RoomId == roomId && (message.CreatedAt < cursor.CreatedAt || (message.CreatedAt == cursor.CreatedAt && message.Id < cursor.Id)))
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(PageSize);

    private async Task StampUnreadAsync(Message message, CancellationToken cancellationToken)
    {
        var cutoff = Now - ConnectionTtl;
        var memberships = await _db.Memberships.Where(membership =>
                membership.RoomId == message.RoomId &&
                membership.UserId != message.CreatorId &&
                membership.Involvement != Involvement.Invisible &&
                (membership.ConnectedAt == null || membership.ConnectedAt < cutoff))
            .ToListAsync(cancellationToken);
        var now = Now;
        foreach (var membership in memberships)
        {
            membership.UnreadAt = message.CreatedAt;
            membership.UpdatedAt = now;
        }
    }

    private async Task BroadcastMessageAsync(Message message, User creator, CancellationToken cancellationToken)
    {
        await _realtime.MessageAsync(Live(message, creator), cancellationToken);
        var memberIds = await _db.Memberships.Where(membership => membership.RoomId == message.RoomId)
            .Select(membership => membership.UserId)
            .ToListAsync(cancellationToken);
        foreach (var memberId in memberIds)
            await _realtime.UnreadAsync(memberId, new LiveUnread { RoomId = message.RoomId }, cancellationToken);
    }

    private static LiveMessage Live(Message message, User creator) => new()
    {
        Id = message.Id,
        RoomId = message.RoomId,
        CreatorId = creator.Id,
        Creator = creator.Name,
        Html = message.Html,
        Text = message.PlainText,
    };

    private async Task ReindexAsync(Message message, CancellationToken cancellationToken)
    {
        var existing = await _db.MessageTokens.Where(token => token.MessageId == message.Id).ToListAsync(cancellationToken);
        _db.MessageTokens.RemoveRange(existing);
        foreach (var term in HtmlText.Terms(message.PlainText).Distinct())
            _db.MessageTokens.Add(new MessageToken { MessageId = message.Id, Term = term });
    }

    private async Task DeleteMessageRowAsync(Message message, CancellationToken cancellationToken)
    {
        var tokens = await _db.MessageTokens.Where(token => token.MessageId == message.Id).ToListAsync(cancellationToken);
        _db.MessageTokens.RemoveRange(tokens);
        var boosts = await _db.Boosts.Where(boost => boost.MessageId == message.Id).ToListAsync(cancellationToken);
        _db.Boosts.RemoveRange(boosts);
        _db.Messages.Remove(message);
        await QuietAsync(() => _realtime.RemovedAsync(new LiveRemoval { Id = message.Id, RoomId = message.RoomId }, cancellationToken));
    }

    private async Task GrantOpenRoomsAsync(User user, CancellationToken cancellationToken)
    {
        var rooms = await _db.Rooms.Where(room => room.Kind == RoomKind.Open).ToListAsync(cancellationToken);
        foreach (var room in rooms)
            await GrantAsync(room, [user], cancellationToken);
    }

    private async Task GrantAsync(Room room, IReadOnlyList<User> users, CancellationToken cancellationToken)
    {
        var existing = await _db.Memberships.Where(membership => membership.RoomId == room.Id)
            .Select(membership => membership.UserId)
            .ToListAsync(cancellationToken);
        var have = existing.ToHashSet();
        var now = Now;
        foreach (var user in users)
        {
            if (!have.Add(user.Id))
                continue;
            _db.Memberships.Add(new Membership
            {
                RoomId = room.Id,
                UserId = user.Id,
                Involvement = room.DefaultInvolvement,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    private async Task<List<User>> MemberUsersAsync(long roomId, CancellationToken cancellationToken) =>
        await _db.Memberships.Where(membership => membership.RoomId == roomId)
            .Select(membership => membership.User)
            .ToListAsync(cancellationToken);

    private async Task<User> RequireUserAsync(long userId, CancellationToken cancellationToken) =>
        await _db.Users.FirstOrDefaultAsync(user => user.Id == userId, cancellationToken)
        ?? throw new AppException(404, "User not found.");

    private async Task<Membership> RequireMembershipAsync(long userId, long roomId, CancellationToken cancellationToken) =>
        await _db.Memberships.FirstOrDefaultAsync(membership => membership.UserId == userId && membership.RoomId == roomId, cancellationToken)
        ?? throw new AppException(404, "Room not found.");

    private async Task<Message> RequireMessageAsync(long roomId, long messageId, CancellationToken cancellationToken) =>
        await _db.Messages.FirstOrDefaultAsync(message => message.Id == messageId && message.RoomId == roomId, cancellationToken)
        ?? throw new AppException(404, "Message not found.");

    private User NewUser(string name, string? email, string? password, UserRole role, DateTime now) => new()
    {
        Name = name,
        EmailAddress = email,
        PasswordHash = password is null ? null : Passwords.Hash(password),
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static async Task QuietAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // Signing out still removes the session when the realtime service is down.
        }
    }

    private static string NewJoinCode()
    {
        var raw = Alphanumeric(12);
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}";
    }

    private static string Alphanumeric(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    private static string UrlToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
