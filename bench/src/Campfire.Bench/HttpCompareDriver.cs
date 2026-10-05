using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campfire.Bench;

public sealed record HttpCompareOptions(
    string Baseline,
    string? Seed,
    string Image,
    string Cpus,
    int Rounds,
    double Duration,
    string Paths,
    IReadOnlyList<int> Concurrencies,
    string ClientCpus,
    string Output,
    string BeforeServer,
    string AfterServer)
{
    public const double WarmupSeconds = 3;

    public bool RunsRails => BeforeServer == "rails" || AfterServer == "rails";

    public bool RunsAsp => BeforeServer == "asp" || AfterServer == "asp";

    public string ServerFor(string side) => side == "before" ? BeforeServer : AfterServer;

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

        var server = options["server"]!;
        if (server is not ("rails" or "asp" or "both"))
            throw new OptionException($"--server must be rails, asp, or both\n{usage}");

        var beforeServer = server == "asp" ? "asp" : "rails";
        var afterServer = server == "rails" ? "rails" : "asp";
        var needsSeed = beforeServer == "rails" || afterServer == "rails";
        if (string.IsNullOrWhiteSpace(options["baseline"]) || (needsSeed && string.IsNullOrWhiteSpace(options["seed"])))
            throw new OptionException($"--baseline and --seed are required\n{usage}");

        return new HttpCompareOptions(
            Path.GetFullPath(options["baseline"]!),
            string.IsNullOrWhiteSpace(options["seed"]) ? null : Path.GetFullPath(options["seed"]!),
            options["image"]!,
            options["cpus"]!,
            rounds,
            duration,
            options["paths"]!,
            concurrencies,
            options["client-cpus"]!,
            OptionReader.Full(options["output"]),
            beforeServer,
            afterServer);
    }

    public static string Usage(string root) =>
        $"""
        Usage: campfire-bench compare-http --baseline PATH --seed PATH [options]
        Compare HTTP throughput with keep-alive clients; every response must be HTTP 200.
        --server rails runs both trees under Puma and Redis. --server asp runs both under
        Kestrel. The default, both, compares the Rails baseline with this checkout's ASP.NET server.

          --baseline PATH
          --seed PATH             Rails fixture; required unless --server asp
          --server VALUE          both
                                  rails   Puma/Redis for the baseline and this checkout
                                  asp     Kestrel for the baseline and this checkout
                                  both    Rails baseline, ASP.NET server for this checkout
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
        ["server"] = "both",
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
        await processes.RunAsync(["taskset", "-pc", options.ClientCpus, Environment.ProcessId.ToString()], cancellationToken: cancellationToken);

        var work = Path.Combine(root, "tmp", "rails-optimization", "http");
        var network = $"cf-bench-{Environment.ProcessId}";
        var redis = $"{network}-redis";
        var app = $"{network}-app";
        JsonDocument? railsLabels = null;
        string? assets = null;
        IRunningProcess? asp = null;

        try
        {
            if (options.RunsAsp)
            {
                foreach (var source in AspSources(options, root))
                {
                    var project = AspNetLaunch.Project(source);
                    if (!File.Exists(project))
                        throw new InvalidOperationException($"{source} has no ASP.NET project at {project}");
                    await processes.RunAsync(AspNetLaunch.Build(source), cancellationToken: cancellationToken);
                }
            }

            if (options.RunsRails)
            {
                railsLabels = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(options.Seed!, "labels.json"), cancellationToken));
                assets = await AssetCache.PrepareAsync(processes, options.Image, root, cancellationToken);
                await processes.RunAsync(["docker", "network", "create", network], cancellationToken: cancellationToken);
                await processes.RunAsync(DockerLaunch.Redis(redis, network), cancellationToken: cancellationToken);
            }

            for (var iteration = 0; iteration < options.Rounds; iteration++)
            {
                var sides = iteration % 2 == 0 ? new[] { "before", "after" } : new[] { "after", "before" };
                foreach (var side in sides)
                {
                    await StopServerAsync();
                    if (options.RunsRails)
                        await processes.RemoveContainerAsync(app, cancellationToken);

                    var port = FreePort();
                    var client = new BenchmarkHttpClient($"http://127.0.0.1:{port}");
                    var source = side == "before" ? options.Baseline : root;
                    JsonDocument? aspLabels = null;
                    try
                    {
                        JsonElement labels;
                        if (options.ServerFor(side) == "rails")
                        {
                            var data = Path.Combine(work, "data");
                            StorageLayout.Prepare(options.Seed!, Path.Combine(data, "storage"));
                            Directory.CreateDirectory(Path.Combine(data, "tmp", "pids"));
                            Directory.CreateDirectory(Path.Combine(data, "log"));
                            await processes.RunAsync(["docker", "exec", redis, "redis-cli", "FLUSHALL"], cancellationToken: cancellationToken);
                            await processes.RunAsync(DockerLaunch.HttpServer(
                                app,
                                network,
                                options.Cpus,
                                port,
                                source,
                                Path.Combine(data, "storage"),
                                Path.Combine(data, "tmp"),
                                Path.Combine(data, "log"),
                                assets!,
                                $"redis://{redis}:6379/0",
                                options.Image), cancellationToken: cancellationToken);
                            labels = railsLabels!.RootElement;
                        }
                        else
                        {
                            var data = Path.Combine(work, "asp-" + side);
                            if (Directory.Exists(data))
                                Directory.Delete(data, recursive: true);
                            await processes.RunAsync(AspNetLaunch.Seed(source, data), cancellationToken: cancellationToken);
                            var database = Path.Combine(data, "campfire.sqlite");
                            var labelsPath = Path.Combine(data, "labels.json");
                            if (!File.Exists(database) || !File.Exists(labelsPath))
                                throw new InvalidOperationException($"ASP.NET seed did not write {database} and {labelsPath}");

                            aspLabels = JsonDocument.Parse(await File.ReadAllTextAsync(labelsPath, cancellationToken));
                            labels = aspLabels.RootElement;
                            asp = await processes.StartAsync(
                                AspNetLaunch.Server(source, options.Cpus),
                                AspNetLaunch.ServerEnvironment(port, database),
                                cancellationToken);
                        }

                        await WaitUntilReadyAsync(
                            client,
                            work,
                            options.ServerFor(side) == "rails"
                                ? async () => (await processes.RunAsync(["docker", "logs", app], cancellationToken: cancellationToken)).StandardOutputText
                                : () => Task.FromResult(asp!.Output),
                            cancellationToken);

                        var results = await MeasureAsync(client, labels, options, cancellationToken);
                        JsonFiles.Write(Path.Combine(options.Output, $"{side}-{iteration + 1}.json"), results);
                        Console.WriteLine($"{iteration + 1}/{options.Rounds}: {side} {options.ServerFor(side)}");
                    }
                    finally
                    {
                        aspLabels?.Dispose();
                    }
                }
            }
        }
        finally
        {
            await StopServerAsync();
            railsLabels?.Dispose();
            if (options.RunsRails)
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

        async Task StopServerAsync()
        {
            if (asp is null)
                return;
            var stopping = asp;
            asp = null;
            await stopping.DisposeAsync();
        }
    }

    private static IEnumerable<string> AspSources(HttpCompareOptions options, string root)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        if (options.BeforeServer == "asp")
            sources.Add(options.Baseline);
        if (options.AfterServer == "asp")
            sources.Add(root);
        return sources;
    }

    private static async Task WaitUntilReadyAsync(
        BenchmarkHttpClient client,
        string work,
        Func<Task<string>> readLog,
        CancellationToken cancellationToken)
    {
        var deadline = MonotonicClock.Seconds() + 45;
        while (!await client.ReadyAsync(cancellationToken))
        {
            if (MonotonicClock.Seconds() > deadline)
            {
                var logPath = Path.Combine(work, "server.log");
                Directory.CreateDirectory(work);
                await File.WriteAllTextAsync(logPath, await readLog(), cancellationToken);
                throw new InvalidOperationException($"server did not become ready; see {logPath}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static async Task<JsonObject> MeasureAsync(
        BenchmarkHttpClient client,
        JsonElement labels,
        HttpCompareOptions options,
        CancellationToken cancellationToken)
    {
        var paths = Workloads.Select(labels, options.Paths);
        var cookie = await client.LoginAsync(
            Workloads.Label(labels, "emails.david"),
            Workloads.Label(labels, "passwords.all"),
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

        return results;
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
