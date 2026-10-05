using Microsoft.EntityFrameworkCore;

namespace Campfire.Core;

public sealed class CampfireDb : DbContext
{
    public CampfireDb(DbContextOptions<CampfireDb> options) : base(options)
    {
    }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Boost> Boosts => Set<Boost>();
    public DbSet<MessageToken> MessageTokens => Set<MessageToken>();
    public DbSet<SearchQuery> SearchQueries => Set<SearchQuery>();
    public DbSet<Draft> Drafts => Set<Draft>();
    public DbSet<Ban> Bans => Set<Ban>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Account>().Property(account => account.Name).HasMaxLength(80);
        model.Entity<Account>().Property(account => account.JoinCode).HasMaxLength(14);

        model.Entity<User>().HasIndex(user => user.EmailAddress).IsUnique();
        model.Entity<User>().HasIndex(user => user.BotToken).IsUnique();
        model.Entity<User>().Property(user => user.Role).HasConversion<int>();
        model.Entity<User>().Property(user => user.Status).HasConversion<int>();
        model.Entity<User>().HasOne(user => user.Webhook).WithOne(webhook => webhook.User).HasForeignKey<Webhook>(webhook => webhook.UserId);

        model.Entity<Session>().HasIndex(session => session.Token).IsUnique();
        model.Entity<Session>().HasOne(session => session.User).WithMany().HasForeignKey(session => session.UserId);

        model.Entity<Room>().Property(room => room.Kind).HasConversion<string>();
        model.Entity<Room>().HasIndex(room => room.DirectKey).IsUnique();
        model.Entity<Room>().HasOne(room => room.Creator).WithMany().HasForeignKey(room => room.CreatorId);

        model.Entity<Membership>().HasIndex(membership => new { membership.RoomId, membership.UserId }).IsUnique();
        model.Entity<Membership>().Property(membership => membership.Involvement).HasConversion<string>();
        model.Entity<Membership>().HasOne(membership => membership.Room).WithMany(room => room.Memberships).HasForeignKey(membership => membership.RoomId);
        model.Entity<Membership>().HasOne(membership => membership.User).WithMany(user => user.Memberships).HasForeignKey(membership => membership.UserId);

        model.Entity<Message>().HasIndex(message => new { message.RoomId, message.ClientMessageId });
        model.Entity<Message>().HasOne(message => message.Room).WithMany(room => room.Messages).HasForeignKey(message => message.RoomId);
        model.Entity<Message>().HasOne(message => message.Creator).WithMany().HasForeignKey(message => message.CreatorId);

        model.Entity<Boost>().HasIndex(boost => new { boost.MessageId, boost.BoosterId }).IsUnique();
        model.Entity<Boost>().Property(boost => boost.Content).HasMaxLength(16);
        model.Entity<Boost>().HasOne(boost => boost.Message).WithMany(message => message.Boosts).HasForeignKey(boost => boost.MessageId);
        model.Entity<Boost>().HasOne(boost => boost.Booster).WithMany().HasForeignKey(boost => boost.BoosterId);

        model.Entity<MessageToken>().HasIndex(token => token.Term);
        model.Entity<MessageToken>().HasIndex(token => new { token.MessageId, token.Term });

        model.Entity<Draft>().HasIndex(draft => new { draft.UserId, draft.RoomId }).IsUnique();
        model.Entity<PushSubscription>().HasIndex(subscription => subscription.Endpoint);

        model.Entity<Attachment>().HasIndex(attachment => attachment.MessageId).IsUnique();
        model.Entity<Attachment>().HasOne(attachment => attachment.Message).WithOne(message => message.Attachment)
            .HasForeignKey<Attachment>(attachment => attachment.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        await Database.EnsureCreatedAsync(cancellationToken);
        await Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "Attachments" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Attachments" PRIMARY KEY AUTOINCREMENT,
                "MessageId" INTEGER NOT NULL,
                "FileName" TEXT NOT NULL,
                "ContentType" TEXT NOT NULL,
                "ByteSize" INTEGER NOT NULL,
                "StorageKey" TEXT NOT NULL,
                "Width" INTEGER NULL,
                "Height" INTEGER NULL,
                CONSTRAINT "FK_Attachments_Messages_MessageId" FOREIGN KEY ("MessageId") REFERENCES "Messages" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Attachments_MessageId" ON "Attachments" ("MessageId");
            """,
            cancellationToken);
    }
}
