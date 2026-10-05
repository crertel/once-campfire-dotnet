using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campfire.Bench;

public static class BenchCli
{
    public static async Task<int> RunAsync(string[] args, IProcessRunner? processes = null, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Overview);
            return 1;
        }

        if (args[0] is "-h" or "--help")
        {
            Console.WriteLine(Overview);
            return 0;
        }

        try
        {
            var command = args[0];
            var rest = args.Skip(1).ToArray();
            processes ??= new SystemProcessRunner();
            switch (command)
            {
                case "compare-http":
                {
                    var root = FindRepositoryRoot();
                    await new HttpCompareDriver(processes).RunAsync(HttpCompareOptions.Parse(rest, root), root, cancellationToken);
                    return 0;
                }
                case "compare-hot-paths":
                {
                    var root = FindRepositoryRoot();
                    await new HotPathCompareDriver(processes).RunAsync(HotPathCompareOptions.Parse(rest, root), root, cancellationToken);
                    return 0;
                }
                case "measure":
                    await MeasureAsync(MeasureOptions.Parse(rest), cancellationToken);
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown command {command}\n{Overview}");
                    return 1;
            }
        }
        catch (HelpException exception)
        {
            Console.WriteLine(exception.Message);
            return 0;
        }
        catch (Exception exception) when (exception is OptionException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "bench", "message_hot_paths.rb")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Run campfire-bench from the once-campfire-dotnet repository.");
    }

    private static async Task MeasureAsync(MeasureOptions options, CancellationToken cancellationToken)
    {
        using var labels = JsonDocument.Parse(File.ReadAllText(Path.Combine(options.Seed, "labels.json")));
        var paths = Workloads.Select(labels.RootElement, options.Paths);
        var client = new BenchmarkHttpClient(options.Base);
        var cookie = await client.LoginAsync(
            Workloads.Label(labels.RootElement, "emails.david"),
            Workloads.Label(labels.RootElement, "passwords.all"),
            cancellationToken);
        var results = new JsonObject();
        foreach (var (name, path) in paths)
        {
            if (options.WarmupSeconds > 0)
                _ = await client.MeasureAsync(path, cookie, concurrency: 1, options.WarmupSeconds, cancellationToken);
            foreach (var concurrency in options.Concurrencies)
            {
                var measurement = await client.MeasureAsync(path, cookie, concurrency, options.Duration, cancellationToken);
                results[$"{name}_{concurrency}"] = measurement.ToJson();
                if (options.Output is not null)
                {
                    Console.WriteLine(
                        "{0}_{1}: {2:0.00} rps p50 {3:0.00} ms",
                        name,
                        concurrency,
                        measurement.RequestsPerSecond,
                        measurement.Latency.P50);
                }
            }
        }

        if (options.Output is null)
            Console.WriteLine(results.ToJsonString(JsonFiles.Pretty));
        else
            JsonFiles.Write(options.Output, results);
    }

    private const string Overview =
        """
        Usage: campfire-bench <command> [options]

          compare-http         Warm HTTP throughput for Rails, ASP.NET, or the two against each other
          compare-hot-paths    In-process rendering, queries, allocations and response parity
          measure              One already-running server, same keep-alive HTTP client

        Every measured response must be HTTP 200 with no transport errors. Responses are
        uncompressed. Login uses the sign-in form's CSRF token. compare-http --server rails
        runs Puma and Redis, --server asp runs Kestrel, and the default both compares the
        Rails baseline with this checkout's ASP.NET server. compare-hot-paths still runs
        bench/message_hot_paths.rb inside the Rails image.
        """;
}

public sealed record MeasureOptions(
    string Base,
    string Seed,
    string Paths,
    IReadOnlyList<int> Concurrencies,
    double Duration,
    double WarmupSeconds,
    string? Output)
{
    public static MeasureOptions Parse(IReadOnlyList<string> args)
    {
        const string usage =
            """
            Usage: campfire-bench measure --base URL --seed PATH [options]
            Measure one running server. Login comes from labels.json.

              --base URL
              --seed PATH
              --paths LIST            room,messages,sidebar,search
              --concurrencies LIST    1,16
              --duration SECONDS      3
              --warmup SECONDS        3
              --output PATH           write JSON here; otherwise JSON goes to stdout
            """;

        var options = OptionReader.Parse(args, new Dictionary<string, string?>
        {
            ["base"] = null,
            ["seed"] = null,
            ["paths"] = "room,messages,sidebar,search",
            ["concurrencies"] = "1,16",
            ["duration"] = "3",
            ["warmup"] = "3",
            ["output"] = null,
        }, usage);

        if (string.IsNullOrWhiteSpace(options["base"]) || string.IsNullOrWhiteSpace(options["seed"]))
            throw new OptionException($"--base and --seed are required\n{usage}");
        if (!double.TryParse(options["duration"], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
            !double.TryParse(options["warmup"], NumberStyles.Float, CultureInfo.InvariantCulture, out var warmup))
            throw new OptionException($"--duration and --warmup must be numbers\n{usage}");

        var concurrencies = new List<int>();
        foreach (var part in (options["concurrencies"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var concurrency) || concurrency <= 0)
                throw new OptionException($"--concurrencies must be positive\n{usage}");
            concurrencies.Add(concurrency);
        }

        if (duration <= 0 || warmup < 0 || concurrencies.Count == 0)
            throw new OptionException($"--duration must be positive\n{usage}");

        return new MeasureOptions(
            options["base"]!,
            Path.GetFullPath(options["seed"]!),
            options["paths"]!,
            concurrencies,
            duration,
            warmup,
            string.IsNullOrWhiteSpace(options["output"]) ? null : Path.GetFullPath(options["output"]!));
    }
}
