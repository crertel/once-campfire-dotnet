using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campfire.Bench;

public sealed record HotPathCompareOptions(
    string Baseline,
    string Seed,
    string? BaselineRef,
    string Image,
    string Cpus,
    int Rounds,
    string Output)
{
    public static HotPathCompareOptions Parse(IReadOnlyList<string> args, string root)
    {
        var usage = Usage(root);
        var options = OptionReader.Parse(args, Defaults(root), usage);
        return new HotPathCompareOptions(
            OptionReader.Required(options, "baseline", usage),
            OptionReader.Required(options, "seed", usage),
            string.IsNullOrWhiteSpace(options["baseline-ref"]) ? null : options["baseline-ref"],
            options["image"]!,
            options["cpus"]!,
            OptionReader.EvenRounds(options["rounds"], usage),
            OptionReader.Full(options["output"]));
    }

    public static string Usage(string root) =>
        $"""
        Usage: campfire-bench compare-hot-paths --baseline PATH --seed PATH [options]
        Compare rendering queries, allocations, timing and response parity.

          --baseline PATH
          --baseline-ref SHA
          --seed PATH
          --image VALUE     {DockerLaunch.Image}
          --cpus VALUE      8-11
          --rounds N        4
          --output PATH     {DefaultOutput(root)}
        """;

    private static Dictionary<string, string?> Defaults(string root) => new()
    {
        ["baseline"] = null,
        ["baseline-ref"] = null,
        ["seed"] = null,
        ["image"] = DockerLaunch.Image,
        ["cpus"] = "8-11",
        ["rounds"] = "4",
        ["output"] = DefaultOutput(root),
    };

    private static string DefaultOutput(string root) =>
        Path.Combine(root, "tmp", "rails-optimization", "results", "requests");
}

public sealed class HotPathCompareDriver(IProcessRunner processes)
{
    public async Task RunAsync(HotPathCompareOptions options, string root, CancellationToken cancellationToken = default)
    {
        var work = Path.Combine(root, "tmp", "rails-optimization", "runtime");
        Directory.CreateDirectory(Path.Combine(work, "tmp"));
        Directory.CreateDirectory(Path.Combine(work, "log"));
        var assets = await AssetCache.PrepareAsync(processes, options.Image, root, cancellationToken);
        var before = new List<JsonObject>();
        var after = new List<JsonObject>();
        var order = new JsonArray();

        for (var iteration = 0; iteration < options.Rounds; iteration++)
        {
            var sides = iteration % 2 == 0 ? new[] { "before", "after" } : new[] { "after", "before" };
            order.Add(new JsonArray(sides.Select(side => JsonValue.Create(side)).ToArray()));
            foreach (var side in sides)
            {
                var storage = Path.Combine(work, "storage");
                StorageLayout.Prepare(options.Seed, storage);
                var source = side == "before" ? options.Baseline : root;
                var output = await processes.RunAsync(DockerLaunch.HotPathProbe(
                    options.Cpus,
                    source,
                    storage,
                    Path.Combine(work, "tmp"),
                    Path.Combine(work, "log"),
                    assets,
                    Path.Combine(root, "bench"),
                    Path.Combine(options.Seed, "labels.json"),
                    options.Image), cancellationToken: cancellationToken);

                var data = ParseProbe(output.StandardOutputText);
                (side == "before" ? before : after).Add(data);
                JsonFiles.Write(Path.Combine(options.Output, $"{side}-{iteration + 1}.json"), data);
                Console.WriteLine($"{iteration + 1}/{options.Rounds}: {side}");
            }
        }

        var results = HotPathSummary.Results(before, after);
        var summary = new JsonObject
        {
            ["metadata"] = await MetadataAsync(options, root, before[0], order, cancellationToken),
            ["results"] = results,
        };
        JsonFiles.Write(Path.Combine(options.Output, "summary.json"), summary);

        foreach (var (name, value) in results)
        {
            var compared = value!.AsObject();
            var beforeMs = compared["before"]!["milliseconds"]!.GetValue<double>();
            var afterMs = compared["after"]!["milliseconds"]!.GetValue<double>();
            var speedup = compared["speedup"]!.GetValue<double>();
            Console.WriteLine(
                "{0}: {1:0.00} -> {2:0.00} ms ({3:0.00}x)",
                name,
                beforeMs,
                afterMs,
                speedup);
        }
    }

    private async Task<JsonObject> MetadataAsync(
        HotPathCompareOptions options,
        string root,
        JsonObject firstBefore,
        JsonArray order,
        CancellationToken cancellationToken)
    {
        var head = (await processes.RunAsync(["git", "-C", root, "rev-parse", "HEAD"], cancellationToken: cancellationToken)).StandardOutputText.Trim();
        var diff = await processes.RunAsync(["git", "-C", root, "diff", "--", "app"], cancellationToken: cancellationToken);
        var imageId = (await processes.RunAsync(
            ["docker", "image", "inspect", "-f", "{{.Id}}", options.Image],
            cancellationToken: cancellationToken)).StandardOutputText.Trim();

        return new JsonObject
        {
            ["baseline_sha"] = options.BaselineRef,
            ["candidate_head"] = head,
            ["candidate_app_diff_sha256"] = Convert.ToHexString(SHA256.HashData(diff.StandardOutput)).ToLowerInvariant(),
            ["platform"] = RuntimeInformation.RuntimeIdentifier,
            ["cpus"] = options.Cpus,
            ["image"] = options.Image,
            ["image_id"] = imageId,
            ["body_and_selected_header_parity"] = "exact across all measured baseline/candidate requests",
            ["order"] = order,
            ["ruby"] = firstBefore["ruby"]?.DeepClone(),
            ["rails"] = firstBefore["rails"]?.DeepClone(),
            ["limits"] = "In-process Rails requests, MemoryStore, frozen clock and fixture-controller CSRF disabled. Fanout excludes adapter I/O. Not network throughput or connection capacity.",
        };
    }

    private static JsonObject ParseProbe(string text)
    {
        try
        {
            return JsonNode.Parse(text)?.AsObject()
                ?? throw new InvalidOperationException("hot-path probe returned empty output");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"hot-path probe did not return JSON: {exception.Message}");
        }
    }
}
