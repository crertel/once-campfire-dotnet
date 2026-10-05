namespace Campfire.Core;

public enum UserRole
{
    Member = 0,
    Administrator = 1,
    Bot = 2,
}

public enum UserStatus
{
    Active = 0,
    Deactivated = 1,
    Banned = 2,
}

public enum RoomKind
{
    Open,
    Closed,
    Direct,
}

public enum Involvement
{
    Invisible,
    Nothing,
    Mentions,
    Everything,
}

public sealed class Account
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string JoinCode { get; set; } = "";
    public string? CustomStyles { get; set; }
    public bool RestrictRoomCreationToAdministrators { get; set; }
    public byte[]? Logo { get; set; }
    public string? LogoContentType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class User
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? EmailAddress { get; set; }
    public string? PasswordHash { get; set; }
    public string? Bio { get; set; }
    public UserRole Role { get; set; }
    public UserStatus Status { get; set; }
    public string? BotToken { get; set; }
    public byte[]? Avatar { get; set; }
    public string? AvatarContentType { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<Membership> Memberships { get; set; } = [];
    public Webhook? Webhook { get; set; }

    public bool IsAdministrator => Role == UserRole.Administrator;
    public bool IsActive => Status == UserStatus.Active;

    public string Initials =>
        string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0])));
}

public sealed class Session
{
    public long Id { get; set; }
    public string Token { get; set; } = "";
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime LastActiveAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Room
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public RoomKind Kind { get; set; }
    public long CreatorId { get; set; }
    public User Creator { get; set; } = null!;
    public string? DirectKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<Membership> Memberships { get; set; } = [];
    public List<Message> Messages { get; set; } = [];

    public Involvement DefaultInvolvement =>
        Kind == RoomKind.Direct ? Involvement.Everything : Involvement.Mentions;
}

public sealed class Membership
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public Involvement Involvement { get; set; } = Involvement.Mentions;
    public DateTime? UnreadAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public int Connections { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool Unread => UnreadAt is not null;
}

public sealed class Message
{
    public long Id { get; set; }
    public long RoomId { get; set; }
    public Room Room { get; set; } = null!;
    public long CreatorId { get; set; }
    public User Creator { get; set; } = null!;
    public string ClientMessageId { get; set; } = "";
    public string Html { get; set; } = "";
    public string PlainText { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<Boost> Boosts { get; set; } = [];
    public Attachment? Attachment { get; set; }
}

public sealed class Attachment
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long ByteSize { get; set; }
    public string StorageKey { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
}

public sealed class Boost
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public long BoosterId { get; set; }
    public User Booster { get; set; } = null!;
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class MessageToken
{
    public long Id { get; set; }
    public long MessageId { get; set; }
    public string Term { get; set; } = "";
}

public sealed class SearchQuery
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Query { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class Draft
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long RoomId { get; set; }
    public string Body { get; set; } = "";
}

public sealed class Ban
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string IpAddress { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class PushSubscription
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Endpoint { get; set; } = "";
    public string? P256dhKey { get; set; }
    public string? AuthKey { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Webhook
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public User User { get; set; } = null!;
    public string? Url { get; set; }
}
