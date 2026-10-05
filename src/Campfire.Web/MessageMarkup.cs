using System.Globalization;
using System.Net;
using System.Text;
using Campfire.Core;

namespace Campfire.Web;

public static class MessageMarkup
{
    public static string One(Message message, string roomLabel, Message? previous, long viewerId)
    {
        var creator = message.Creator;
        var name = creator?.Name ?? "";
        var avatar = AvatarUrl(creator);
        var instant = Instant(message.CreatedAt);
        var previousInstant = previous is null ? (DateTimeOffset?)null : Instant(previous.CreatedAt);
        var firstOfDay = previousInstant is null || LocalDay(instant) != LocalDay(previousInstant.Value);
        var threaded = previous is not null
            && previous.CreatorId == message.CreatorId
            && Math.Abs((instant - previousInstant!.Value).TotalMinutes) <= 5;
        var mine = viewerId > 0 && message.CreatorId == viewerId;
        var roomUrl = "/rooms/" + message.RoomId;
        var stamp = instant.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var css = "message message--formatted";
        if (firstOfDay)
            css += " message--first-of-day";
        if (mine)
            css += " message--me";
        if (threaded)
            css += " message--threaded";
        var builder = new StringBuilder();
        builder.Append("<div id=\"message-").Append(message.Id).Append("\" class=\"").Append(css)
            .Append("\" data-user-id=\"").Append(creator?.Id ?? 0)
            .Append("\" data-message-id=\"").Append(message.Id)
            .Append("\" data-message-timestamp=\"").Append(instant.ToUnixTimeMilliseconds()).Append("\">");
        builder.Append("<h2 class=\"message__day-separator\"><time datetime=\"").Append(stamp)
            .Append("\" data-local-time-target=\"date\"></time></h2>");
        builder.Append("<figure class=\"avatar message__avatar\"><a class=\"btn avatar\" href=\"/users/")
            .Append(creator?.Id ?? 0).Append("\" title=\"").Append(Encode(name))
            .Append("\"><img src=\"").Append(Encode(avatar)).Append("\" alt=\"\" width=\"48\" height=\"48\"></a></figure>");
        builder.Append("<div class=\"message__body\"><div class=\"message__body-content\"><div class=\"message__meta\"><h3 class=\"message__heading\">");
        builder.Append("<span class=\"message__author\" title=\"").Append(Encode(name)).Append("\"><strong>")
            .Append(Encode(name)).Append("</strong></span>");
        builder.Append("<a class=\"message__permalink\" href=\"").Append(roomUrl).Append("/@").Append(message.Id).Append("\"><time class=\"message__timestamp\" datetime=\"")
            .Append(stamp).Append("\" data-local-time-target=\"time\"></time></a>");
        builder.Append("<span class=\"message__room\"><a href=\"").Append(roomUrl).Append("\">")
            .Append(Encode(roomLabel)).Append("</a></span>");
        builder.Append("</h3></div>");
        builder.Append("<div dir=\"auto\" data-messages-target=\"body\">").Append(Presentation.Body(message)).Append("</div>");
        builder.Append("<div class=\"boosts flex flex-wrap align-center gap full-width\">");
        foreach (var boost in message.Boosts.OrderBy(item => item.CreatedAt))
        {
            var booster = boost.Booster;
            builder.Append("<div class=\"boost boost-item flex-inline max-width align-center fill-white gap\" id=\"boost-")
                .Append(boost.Id).Append("\">");
            builder.Append("<figure class=\"avatar boost__avatar\"><img src=\"").Append(Encode(AvatarUrl(booster)))
                .Append("\" alt=\"").Append(Encode((booster?.Name ?? "") + " boosted")).Append("\" width=\"20\" height=\"20\"></figure>");
            builder.Append("<span class=\"txt-small\">").Append(Encode(boost.Content)).Append("</span></div>");
        }

        builder.Append("</div></div></div></div>");
        return builder.ToString();
    }

    public static string AvatarUrl(User? user)
    {
        if (user is null)
            return "/assets/images/default-avatar.svg";
        if (user.Avatar is { Length: > 0 })
            return "/users/" + user.Id + "/avatar";
        return user.Role == UserRole.Bot
            ? "/assets/images/default-bot-avatar.svg"
            : "/assets/images/default-avatar.svg";
    }

    public static string RoomLabel(Room room, long userId)
    {
        if (room.Kind != RoomKind.Direct)
            return string.IsNullOrEmpty(room.Name) ? "Room" : room.Name;
        var names = room.Memberships
            .Where(membership => membership.UserId != userId && membership.User is not null)
            .Select(membership => FirstName(membership.User.Name))
            .Where(name => name.Length > 0)
            .ToList();
        return names.Count == 0 ? "Direct" : string.Join(", ", names);
    }

    public static string FirstName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        return name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static DateTimeOffset Instant(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return new DateTimeOffset(utc);
    }

    private static DateOnly LocalDay(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

public sealed record SetupForm(string Csrf, string Action);

public sealed record InviteForm(string Code, string Url, bool Administrator);
