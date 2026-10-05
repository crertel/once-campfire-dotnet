using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Campfire.Core;

public static class Seeder
{
    public const string Email = "david@37signals.com";
    public const string Password = "secret123456";

    public static async Task WriteAsync(string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "campfire.sqlite");
        if (File.Exists(databasePath))
            File.Delete(databasePath);

        var options = new DbContextOptionsBuilder<CampfireDb>().UseSqlite(Connection(databasePath)).Options;
        await using var db = new CampfireDb(options);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        var clock = new ManualTime();
        var app = new CampfireApp(db, clock, new NoopRealtime());
        var david = await app.FirstRunAsync("David", Email, Password, cancellationToken);
        var room = await db.Rooms.SingleAsync(cancellationToken);
        room.Name = "Watercooler";
        await db.SaveChangesAsync(cancellationToken);

        long busy = 0;
        for (var i = 1; i <= 80; i++)
        {
            var body = i == 30 ? "Time for coffee" : $"message {i}";
            var message = await app.CreateMessageAsync(david.Id, room.Id, body, null, cancellationToken);
            message.CreatedAt = clock.Now.UtcDateTime;
            message.UpdatedAt = message.CreatedAt;
            await db.SaveChangesAsync(cancellationToken);
            if (i == 60)
                busy = message.Id;
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var labels = new JsonObject
        {
            ["passwords.all"] = Password,
            ["emails.david"] = Email,
            ["rooms.watercooler"] = room.Id,
            ["messages.busy_060"] = busy,
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "labels.json"), labels.ToJsonString(), cancellationToken);
    }

    public static string Connection(string databasePath) => $"Data Source={databasePath}";
}
