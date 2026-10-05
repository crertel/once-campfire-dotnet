using System.Data.Common;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Campfire.Core;

public sealed class QueryCounter : DbCommandInterceptor
{
    public int Count { get; private set; }

    public void Reset() => Count = 0;

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Count++;
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Count++;
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Count++;
        return base.ScalarExecuting(command, eventData, result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Count++;
        return base.NonQueryExecuting(command, eventData, result);
    }
}

public static class HotPaths
{
    public static async Task<string> MeasureAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var counter = new QueryCounter();
        var options = new DbContextOptionsBuilder<CampfireDb>()
            .UseSqlite(Seeder.Connection(databasePath))
            .AddInterceptors(counter)
            .Options;
        await using var db = new CampfireDb(options);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        var app = new CampfireApp(db, TimeProvider.System, new NoopRealtime());
        if (!await db.Users.AnyAsync(user => user.EmailAddress == Seeder.Email, cancellationToken))
            await SeedBenchUserAsync(db, cancellationToken);

        var user = await db.Users.FirstAsync(item => item.EmailAddress == Seeder.Email, cancellationToken);
        var room = await db.Rooms.OrderBy(item => item.Id).FirstAsync(cancellationToken);
        var before = await db.Messages.Where(message => message.RoomId == room.Id).OrderBy(message => message.Id).Skip(59).Select(message => message.Id).FirstAsync(cancellationToken);

        var results = new JsonObject
        {
            ["room"] = Measure(() => app.ReadLastPage(room.Id), counter),
            ["messages"] = Measure(() => app.ReadBefore(room.Id, before), counter),
            ["sidebar"] = Measure(() => app.ReadSidebar(user.Id), counter),
            ["search"] = Measure(() => app.ReadSearch(user.Id, "coffee"), counter),
        };
        return new JsonObject { ["results"] = results }.ToJsonString();
    }

    private static async Task SeedBenchUserAsync(CampfireDb db, CancellationToken cancellationToken)
    {
        var clock = new ManualTime();
        var seeding = new CampfireApp(db, clock, new NoopRealtime());
        var david = await seeding.FirstRunAsync("David", Seeder.Email, Seeder.Password, cancellationToken);
        var room = await db.Rooms.SingleAsync(cancellationToken);
        room.Name = "Watercooler";
        await db.SaveChangesAsync(cancellationToken);
        for (var i = 1; i <= 80; i++)
        {
            var body = i == 30 ? "Time for coffee" : $"message {i}";
            var message = await seeding.CreateMessageAsync(david.Id, room.Id, body, null, cancellationToken);
            message.CreatedAt = clock.Now.UtcDateTime;
            message.UpdatedAt = message.CreatedAt;
            await db.SaveChangesAsync(cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private static JsonObject Measure(Action action, QueryCounter counter)
    {
        const int rounds = 3;
        const int samples = 5;
        action();
        var roundMilliseconds = new double[rounds];
        var roundQueries = new double[rounds];
        var roundAllocations = new double[rounds];
        for (var round = 0; round < rounds; round++)
        {
            var milliseconds = new double[samples];
            var queries = new double[samples];
            var allocations = new double[samples];
            for (var sample = 0; sample < samples; sample++)
            {
                counter.Reset();
                var before = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                action();
                milliseconds[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                queries[sample] = counter.Count;
                allocations[sample] = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            roundMilliseconds[round] = Median(milliseconds);
            roundQueries[round] = Median(queries);
            roundAllocations[round] = Median(allocations);
        }

        var medians = new JsonArray();
        foreach (var value in roundMilliseconds)
            medians.Add(value);

        return new JsonObject
        {
            ["milliseconds"] = Median(roundMilliseconds),
            ["queries"] = Median(roundQueries),
            ["allocations"] = Median(roundAllocations),
            ["round_medians_ms"] = medians,
        };
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
