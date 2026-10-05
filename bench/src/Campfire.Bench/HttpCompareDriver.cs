using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campfire.Bench;

public sealed record HttpCompareOptions(
    string Baseline,
    string Seed,
    string Image,
    string Cpus,
    int Rounds,
    double Duration,
    string Paths,
    IReadOnlyList<int> Concurrencies,
    string ClientCpus,
    string Output)
{
    public const double WarmupSeconds = 3;

    public static HttpCompareOptions Parse(IReadOnlyList<string> args, string root)
    {
        var usage = Usage(root);
        var options = OptionReader.Parse(args, Defaults(root), usage);
        var rounds = OptionReader.EvenRounds(options["rounds"], usage);
        if (!double.TryParse(options["duration"], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
            throw new OptionException($"--duration and --concurrencies must be positive\n{usage}");

        var concurrencies = new List<int>();
        foreach (var part in (options["concurrencies"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var concurrency))
                throw new OptionException($"--duration and --concurrencies must be positive\n{usage}");
            concurrencies.Add(concurrency);
        }

        if (duration <= 0 || concurrencies.Count == 0 || concurrencies.Any(concurrency => concurrency <= 0))
            throw new OptionException($"--duration and --concurrencies must be positive\n{usage}");

        return new HttpCompareOptions(
            OptionReader.Required(options, "baseline", usage),
            OptionReader.Required(options, "seed", usage),
            options["image"]!,
            options["cpus"]!,
            rounds,
            duration,
            options["paths"]!,
            concurrencies,
            options["client-cpus"]!,
            OptionReader.Full(options["output"]));
    }

    public static string Usage(string root) =>
        $"""
        Usage: campfire-bench compare-http --baseline PATH --seed PATH [options]
        Compare HTTP throughput with keep-alive clients; every response must be HTTP 200.

          --baseline PATH
          --seed PATH
          --image VALUE           {DockerLaunch.Image}
          --cpus VALUE            8-11
          --rounds N              2
          --duration SECONDS      3
          --paths LIST            room,messages,sidebar,search
          --concurrencies LIST    1,16
          --client-cpus VALUE     12-15
          --output PATH           {DefaultOutput(root)}
        """;

    private static Dictionary<string, string?> Defaults(string root) => new()
    {
        ["baseline"] = null,
        ["seed"] = null,
        ["image"] = DockerLaunch.Image,
        ["cpus"] = "8-11",
        ["rounds"] = "2",
        ["duration"] = "3",
        ["paths"] = "room,messages,sidebar,search",
        ["concurrencies"] = "1,16",
        ["client-cpus"] = "12-15",
        ["output"] = DefaultOutput(root),
    };

    private static string DefaultOutput(string root) =>
        Path.Combine(root, "tmp", "rails-optimization", "results", "http");
}

public sealed class HttpCompareDriver(IProcessRunner processes)
{
    public async Task RunAsync(HttpCompareOptions options, string root, CancellationToken cancellationToken = default)
    {
        using var labels = JsonDocument.Parse(File.ReadAllText(Path.Combine(options.Seed, "labels.json")));
        var paths = Workloads.Select(labels.RootElement, options.Paths);
        await processes.RunAsync(["taskset", "-pc", options.ClientCpus, Environment.ProcessId.ToString()], cancellationToken: cancellationToken);

        var work = Path.Combine(root, "tmp", "rails-optimization", "http");
        var assets = await AssetCache.PrepareAsync(processes, options.Image, root, cancellationToken);
        var network = $"cf-bench-{Environment.ProcessId}";
        var redis = $"{network}-redis";
        var app = $"{network}-app";

        try
        {
            await processes.RunAsync(["docker", "network", "create", network], cancellationToken: cancellationToken);
            await processes.RunAsync(DockerLaunch.Redis(redis, network), cancellationToken: cancellationToken);

            for (var iteration = 0; iteration < options.Rounds; iteration++)
            {
                var sides = iteration % 2 == 0 ? new[] { "before", "after" } : new[] { "after", "before" };
                foreach (var side in sides)
                {
                    await processes.RemoveContainerAsync(app, cancellationToken);
                    var data = Path.Combine(work, "data");
                    StorageLayout.Prepare(options.Seed, Path.Combine(data, "storage"));
                    Directory.CreateDirectory(Path.Combine(data, "tmp", "pids"));
                    Directory.CreateDirectory(Path.Combine(data, "log"));
                    await processes.RunAsync(["docker", "exec", redis, "redis-cli", "FLUSHALL"], cancellationToken: cancellationToken);

                    var port = FreePort();
                    var client = new BenchmarkHttpClient($"http://127.0.0.1:{port}");
                    var source = side == "before" ? options.Baseline : root;
                    await processes.RunAsync(DockerLaunch.HttpServer(
                        app,
                        network,
                        options.Cpus,
                        port,
                        source,
                        Path.Combine(data, "storage"),
                        Path.Combine(data, "tmp"),
                        Path.Combine(data, "log"),
                        assets,
                        $"redis://{redis}:6379/0",
                        options.Image), cancellationToken: cancellationToken);

                    var deadline = MonotonicClock.Seconds() + 45;
                    while (!await client.ReadyAsync(cancellationToken))
                    {
                        if (MonotonicClock.Seconds() > deadline)
                        {
                            var logs = await processes.RunAsync(["docker", "logs", app], cancellationToken: cancellationToken);
                            var logPath = Path.Combine(work, "server.log");
                            Directory.CreateDirectory(work);
                            await File.WriteAllTextAsync(logPath, logs.StandardOutputText, cancellationToken);
                            throw new InvalidOperationException($"server did not become ready; see {logPath}");
                        }

                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                    }

                    var cookie = await client.LoginAsync(
                        Workloads.Label(labels.RootElement, "emails.david"),
                        Workloads.Label(labels.RootElement, "passwords.all"),
                        cancellationToken);
                    var results = new JsonObject();
                    foreach (var (name, path) in paths)
                    {
                        _ = await client.MeasureAsync(path, cookie, concurrency: 1, HttpCompareOptions.WarmupSeconds, cancellationToken);
                        foreach (var concurrency in options.Concurrencies)
                        {
                            var measurement = await client.MeasureAsync(path, cookie, concurrency, options.Duration, cancellationToken);
                            results[$"{name}_{concurrency}"] = measurement.ToJson();
                        }
                    }

                    JsonFiles.Write(Path.Combine(options.Output, $"{side}-{iteration + 1}.json"), results);
                    Console.WriteLine($"{iteration + 1}/{options.Rounds}: {side}");
                }
            }
        }
        finally
        {
            await processes.RemoveContainerAsync(app, CancellationToken.None);
            await processes.RemoveContainerAsync(redis, CancellationToken.None);
            try
            {
                await processes.RunAsync(["docker", "network", "rm", network], cancellationToken: CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
