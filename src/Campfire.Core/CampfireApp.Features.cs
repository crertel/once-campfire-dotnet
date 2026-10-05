using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Campfire.Core;

public sealed partial class CampfireApp
{
    public static Involvement NextInvolvement(RoomKind kind, Involvement current)
    {
        if (kind == RoomKind.Direct)
            return current == Involvement.Everything ? Involvement.Nothing : Involvement.Everything;
        return current switch
        {
            Involvement.Mentions => Involvement.Everything,
            Involvement.Everything => Involvement.Nothing,
            Involvement.Nothing => Involvement.Invisible,
            _ => Involvement.Mentions,
        };
    }

    public async Task<Room> SaveRoomAsync(long actorId, long roomId, RoomKind kind, string? name, IReadOnlyCollection<long>? memberIds, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        var room = await _db.Rooms.Include(item => item.Memberships).FirstAsync(item => item.Id == roomId, cancellationToken);
        if (room.Kind == RoomKind.Direct)
            throw new AppException(422, "can't be changed for a direct room");
        if (!actor.IsAdministrator && actor.Id != room.CreatorId)
            throw new AppException(403, "You cannot change this room.");
        if (kind == RoomKind.Direct)
            throw new AppException(422, "A shared room cannot become a direct room.");
        if (string.IsNullOrWhiteSpace(name))
            throw new AppException(422, "Name is required.");

        room.Name = name.Trim();
        var opening = room.Kind != RoomKind.Open && kind == RoomKind.Open;
        room.Kind = kind;
        room.UpdatedAt = Now;
        if (opening)
        {
            var active = await _db.Users.Where(user => user.Status == UserStatus.Active).ToListAsync(cancellationToken);
            await GrantAsync(room, active, cancellationToken);
        }
        else if (kind == RoomKind.Closed && memberIds is not null)
            await ReviseAsync(room, memberIds.Append(actor.Id).Distinct().ToArray(), cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        return room;
    }

    public async Task DeleteRoomAsync(long actorId, long roomId, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        var room = await _db.Rooms.FirstAsync(item => item.Id == roomId, cancellationToken);
        var member = await _db.Memberships.AnyAsync(item => item.RoomId == roomId && item.UserId == actorId, cancellationToken);
        if (!member)
            throw new AppException(404, "Room not found.");
        if (!actor.IsAdministrator && actor.Id != room.CreatorId)
            throw new AppException(403, "You cannot change this room.");

        var messages = await _db.Messages.Where(message => message.RoomId == roomId).ToListAsync(cancellationToken);
        foreach (var message in messages)
            await DeleteMessageRowAsync(message, cancellationToken);
        var drafts = await _db.Drafts.Where(draft => draft.RoomId == roomId).ToListAsync(cancellationToken);
        _db.Drafts.RemoveRange(drafts);
        var memberships = await _db.Memberships.Where(item => item.RoomId == roomId).ToListAsync(cancellationToken);
        _db.Memberships.RemoveRange(memberships);
        _db.Rooms.Remove(room);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MessagePage> MessagesAroundAsync(long roomId, long messageId, CancellationToken cancellationToken = default)
    {
        var message = await _db.Messages.Include(item => item.Creator).Include(item => item.Attachment)
            .Include(item => item.Boosts).ThenInclude(boost => boost.Booster)
            .FirstOrDefaultAsync(item => item.Id == messageId && item.RoomId == roomId, cancellationToken);
        if (message is null)
            return await MessagesPageAsync(roomId, null, null, cancellationToken);

        var before = await BeforeQuery(roomId, message).Include(item => item.Creator).Include(item => item.Attachment)
            .Include(item => item.Boosts).ThenInclude(boost => boost.Booster).ToListAsync(cancellationToken);
        before.Reverse();
        var after = await _db.Messages.Where(item => item.RoomId == roomId && (item.CreatedAt > message.CreatedAt || (item.CreatedAt == message.CreatedAt && item.Id > message.Id)))
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Take(PageSize)
            .Include(item => item.Creator).Include(item => item.Attachment)
            .Include(item => item.Boosts).ThenInclude(boost => boost.Booster)
            .ToListAsync(cancellationToken);
        before.Add(message);
        before.AddRange(after);
        return new MessagePage(true, before);
    }

    public async Task<IReadOnlyList<User>> AutocompleteAsync(long? roomId, string? query, CancellationToken cancellationToken = default)
    {
        var users = _db.Users.Where(user => user.Status == UserStatus.Active && user.Role != UserRole.Bot);
        if (roomId is not null)
        {
            var ids = _db.Memberships.Where(membership => membership.RoomId == roomId).Select(membership => membership.UserId);
            users = users.Where(user => ids.Contains(user.Id));
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            users = users.Where(user => user.Name.ToLower().Contains(term));
        }
        return await users.OrderBy(user => user.Name).Take(20).ToListAsync(cancellationToken);
    }

    public async Task<User> UpdateBotAsync(long actorId, long botId, string name, string? webhookUrl, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can edit a bot.");
        if (string.IsNullOrWhiteSpace(name))
            throw new AppException(422, "Name is required.");
        var bot = await _db.Users.Include(user => user.Webhook).FirstAsync(user => user.Id == botId && user.Role == UserRole.Bot, cancellationToken);
        bot.Name = name.Trim();
        bot.UpdatedAt = Now;
        var url = string.IsNullOrWhiteSpace(webhookUrl) ? null : webhookUrl.Trim();
        if (url is null)
        {
            if (bot.Webhook is not null)
                _db.Webhooks.Remove(bot.Webhook);
        }
        else if (bot.Webhook is null)
            _db.Webhooks.Add(new Webhook { UserId = bot.Id, Url = url });
        else
            bot.Webhook.Url = url;
        await _db.SaveChangesAsync(cancellationToken);
        return bot;
    }

    public async Task DeactivateBotAsync(long actorId, long botId, CancellationToken cancellationToken = default)
    {
        var actor = await RequireUserAsync(actorId, cancellationToken);
        if (!actor.IsAdministrator)
            throw new AppException(403, "Only an administrator can remove a bot.");
        var bot = await _db.Users.FirstAsync(user => user.Id == botId && user.Role == UserRole.Bot, cancellationToken);
        bot.Status = UserStatus.Deactivated;
        bot.UpdatedAt = Now;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<User>> BotsAsync(CancellationToken cancellationToken = default) =>
        await _db.Users.Include(user => user.Webhook).Where(user => user.Role == UserRole.Bot && user.Status == UserStatus.Active)
            .OrderBy(user => user.Name).ToListAsync(cancellationToken);

    public async Task<int> MessageCountAsync(long roomId, CancellationToken cancellationToken = default) =>
        await _db.Messages.CountAsync(message => message.RoomId == roomId, cancellationToken);

    public async Task<bool> HasOlderAsync(IReadOnlyList<Message> page, CancellationToken cancellationToken = default)
    {
        if (page.Count == 0)
            return false;
        var first = page[0];
        var roomId = first.RoomId;
        var createdAt = first.CreatedAt;
        var id = first.Id;
        return await _db.Messages.AnyAsync(message => message.RoomId == roomId && (message.CreatedAt < createdAt || (message.CreatedAt == createdAt && message.Id < id)), cancellationToken);
    }

    public async Task<bool> HasNewerAsync(IReadOnlyList<Message> page, CancellationToken cancellationToken = default)
    {
        if (page.Count == 0)
            return false;
        var last = page[page.Count - 1];
        var roomId = last.RoomId;
        var createdAt = last.CreatedAt;
        var id = last.Id;
        return await _db.Messages.AnyAsync(message => message.RoomId == roomId && (message.CreatedAt > createdAt || (message.CreatedAt == createdAt && message.Id > id)), cancellationToken);
    }

    public string IssueTransfer(long userId)
    {
        var secrets = _secrets ?? throw new AppException(500, "Transfer signing is not configured.");
        return Transfers.Issue(userId, _time.GetUtcNow().Add(Transfers.Lifetime), secrets.TransferKey);
    }

    public async Task<User?> RedeemTransferAsync(string token, CancellationToken cancellationToken = default)
    {
        var secrets = _secrets ?? throw new AppException(500, "Transfer signing is not configured.");
        var userId = Transfers.Read(token, secrets.TransferKey, _time.GetUtcNow());
        if (userId is null)
            return null;
        return await _db.Users.FirstOrDefaultAsync(user => user.Id == userId && user.Status == UserStatus.Active, cancellationToken);
    }

    public async Task<byte[]?> ReadAttachmentAsync(long messageId, CancellationToken cancellationToken = default)
    {
        var attachment = await _db.Attachments.FirstOrDefaultAsync(item => item.MessageId == messageId, cancellationToken);
        if (attachment is null || _files is null)
            return null;
        return _files.Read(attachment.StorageKey);
    }

    public async Task<Attachment?> AttachmentAsync(long messageId, CancellationToken cancellationToken = default) =>
        await _db.Attachments.FirstOrDefaultAsync(item => item.MessageId == messageId, cancellationToken);

    private async Task<Attachment> StoreAttachmentAsync(Message message, IncomingFile file, CancellationToken cancellationToken)
    {
        if (_files is null)
            throw new AppException(422, "File storage is not configured.");
        var name = SafeFileName(file.FileName);
        var sniffed = ImageSniff.ContentType(file.Bytes);
        var contentType = sniffed ?? SafeContentType(file.ContentType);
        var size = ImageSniff.Size(file.Bytes);
        var key = await _files.SaveAsync(file.Bytes, cancellationToken);
        var attachment = new Attachment
        {
            MessageId = message.Id,
            FileName = name,
            ContentType = contentType,
            ByteSize = file.Bytes.Length,
            StorageKey = key,
            Width = size?.Width,
            Height = size?.Height,
        };
        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync(cancellationToken);
        return attachment;
    }

    private async Task ReviseAsync(Room room, IReadOnlyCollection<long> memberIds, CancellationToken cancellationToken)
    {
        var wanted = memberIds.ToHashSet();
        wanted.Add(room.CreatorId);
        var current = await _db.Memberships.Where(membership => membership.RoomId == room.Id).ToListAsync(cancellationToken);
        foreach (var membership in current)
        {
            if (!wanted.Contains(membership.UserId))
                _db.Memberships.Remove(membership);
        }
        var users = await _db.Users.Where(user => wanted.Contains(user.Id) && user.Status == UserStatus.Active).ToListAsync(cancellationToken);
        await GrantAsync(room, users, cancellationToken);
    }

    private async Task FanOutAsync(Message message, User creator, CancellationToken cancellationToken)
    {
        if (_outbound is null)
            return;
        var room = await _db.Rooms.FirstAsync(item => item.Id == message.RoomId, cancellationToken);
        var memberships = await _db.Memberships.Include(item => item.User).ThenInclude(user => user.Webhook)
            .Where(item => item.RoomId == room.Id && item.UserId != creator.Id)
            .ToListAsync(cancellationToken);
        var mentioned = Mentionees(message, memberships.Select(item => item.User).ToList());
        foreach (var membership in memberships)
        {
            var user = membership.User;
            var webhook = user.Webhook?.Url;
            if (user.Role != UserRole.Bot || user.Status != UserStatus.Active || string.IsNullOrWhiteSpace(webhook))
                continue;
            if (room.Kind != RoomKind.Direct && !mentioned.Contains(user.Id))
                continue;
            _outbound.EnqueueWebhook(new WebhookCall(user.Id, room.Id, webhook, WebhookJson(message, creator, room, user)));
        }

        var now = Now;
        var memberIds = memberships.Select(membership => membership.UserId).ToArray();
        var subscriptions = await _db.PushSubscriptions.Where(item => memberIds.Contains(item.UserId)).ToListAsync(cancellationToken);
        foreach (var membership in memberships)
        {
            if (membership.Involvement is Involvement.Nothing or Involvement.Invisible)
                continue;
            if (IsConnected(membership, now))
                continue;
            if (membership.Involvement == Involvement.Mentions && !mentioned.Contains(membership.UserId))
                continue;
            var unread = await _db.Memberships.CountAsync(item => item.UserId == membership.UserId && item.UnreadAt != null, cancellationToken);
            var title = room.Kind == RoomKind.Direct ? creator.Name : room.Name ?? "Campfire";
            var body = room.Kind == RoomKind.Direct ? message.PlainText : creator.Name + ": " + message.PlainText;
            foreach (var subscription in subscriptions.Where(item => item.UserId == membership.UserId))
            {
                if (string.IsNullOrEmpty(subscription.P256dhKey) || string.IsNullOrEmpty(subscription.AuthKey))
                    continue;
                _outbound.EnqueuePush(new PushCall(
                    subscription.Endpoint,
                    subscription.P256dhKey,
                    subscription.AuthKey,
                    title,
                    body,
                    "/rooms/" + room.Id + "/@" + message.Id,
                    unread));
            }
        }
    }

    private static HashSet<long> Mentionees(Message message, IReadOnlyList<User> members)
    {
        var ids = new HashSet<long>();
        foreach (Match match in MentionId().Matches(message.Html))
        {
            if (long.TryParse(match.Groups["id"].Value, out var id))
                ids.Add(id);
        }
        foreach (var member in members)
        {
            if (member.Name.Length > 0 && message.PlainText.Contains("@" + member.Name, StringComparison.Ordinal))
                ids.Add(member.Id);
        }
        return ids;
    }

    private static string WebhookJson(Message message, User creator, Room room, User bot)
    {
        var plain = message.PlainText.Replace("@" + bot.Name, "", StringComparison.Ordinal).Trim();
        return JsonSerializer.Serialize(new
        {
            user = new { id = creator.Id, name = creator.Name },
            room = new { id = room.Id, name = room.Name, path = "/rooms/" + room.Id + "/" + BotKey(bot) + "/messages" },
            message = new
            {
                id = message.Id,
                body = new { html = message.Html, plain },
                path = "/rooms/" + room.Id + "/@" + message.Id,
            },
        });
    }

    private static string SafeFileName(string name)
    {
        var file = Path.GetFileName(name.Replace('\\', '/'));
        var cleaned = new string(file.Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "attachment" : cleaned;
    }

    private static string SafeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return "application/octet-stream";
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return ContentTypePattern().IsMatch(type) ? type : "application/octet-stream";
    }

    [GeneratedRegex(@"data-user-id=""(?<id>\d+)""", RegexOptions.CultureInvariant)]
    private static partial Regex MentionId();

    [GeneratedRegex(@"\A[\w.+-]+/[\w.+-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContentTypePattern();
}
