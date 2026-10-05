using System.Text.Json;

namespace Campfire.Bench.Tests;

public class WorkloadsAndStorageTests
{
    [Fact]
    public void Paths_come_from_labels()
    {
        using var labels = JsonDocument.Parse("""{"rooms.watercooler":7,"messages.busy_060":"060"}""");

        var paths = Workloads.Select(labels.RootElement, "room,messages,sidebar,search");

        Assert.Equal("/rooms/7", paths["room"]);
        Assert.Equal("/rooms/7/messages?before=060", paths["messages"]);
        Assert.Equal("/users/me/sidebar", paths["sidebar"]);
        Assert.Equal("/searches?q=coffee", paths["search"]);
    }

    [Fact]
    public void Unknown_or_repeated_paths_are_rejected()
    {
        using var labels = JsonDocument.Parse("""{"rooms.watercooler":"1","messages.busy_060":"2"}""");

        Assert.Throws<InvalidOperationException>(() => Workloads.Select(labels.RootElement, "room,nope"));
        Assert.Throws<InvalidOperationException>(() => Workloads.Select(labels.RootElement, "room,room"));
        Assert.Throws<InvalidOperationException>(() => Workloads.Select(labels.RootElement, ""));
    }

    [Fact]
    public void Prepare_copies_seed_db_and_storage_into_the_rails_layout()
    {
        var root = Directory.CreateTempSubdirectory("campfire-bench-");
        try
        {
            var seed = Path.Combine(root.FullName, "seed");
            Directory.CreateDirectory(Path.Combine(seed, "db"));
            Directory.CreateDirectory(Path.Combine(seed, "storage", "nested"));
            File.WriteAllText(Path.Combine(seed, "db", "production.sqlite3"), "db");
            File.WriteAllText(Path.Combine(seed, "storage", "nested", "blob"), "file");

            var destination = Path.Combine(root.FullName, "out");
            StorageLayout.Prepare(seed, destination);

            Assert.Equal("db", File.ReadAllText(Path.Combine(destination, "db", "production.sqlite3")));
            Assert.Equal("file", File.ReadAllText(Path.Combine(destination, "files", "nested", "blob")));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Cli_help_and_missing_arguments_do_not_launch_anything()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            Assert.Equal(0, await BenchCli.RunAsync(["--help"]));
            Assert.Contains("compare-http", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("ASP.NET", stdout.ToString(), StringComparison.Ordinal);

            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            Assert.Equal(0, await BenchCli.RunAsync(["compare-http", "--help"]));
            Assert.Contains("--baseline", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("--server", stdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("ASP.NET server", stdout.ToString(), StringComparison.Ordinal);

            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            Assert.Equal(1, await BenchCli.RunAsync(["compare-http"]));
            Assert.Contains("--baseline and --seed are required", stderr.ToString(), StringComparison.Ordinal);

            stdout.GetStringBuilder().Clear();
            stderr.GetStringBuilder().Clear();
            Assert.Equal(1, await BenchCli.RunAsync(["compare-hot-paths", "--baseline", "/tmp", "--seed", "/tmp", "--rounds", "3"]));
            Assert.Contains("--rounds must be even", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }
}
