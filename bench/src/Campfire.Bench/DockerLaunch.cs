namespace Campfire.Bench;

public static class AssetCache
{
    public static async Task<string> PrepareAsync(
        IProcessRunner processes,
        string image,
        string root,
        CancellationToken cancellationToken)
    {
        var assets = Path.Combine(root, "tmp", "rails-optimization", "runtime", "assets");
        Directory.CreateDirectory(assets);
        if (Directory.EnumerateFileSystemEntries(assets).Any())
            return assets;

        var archive = await processes.RunAsync(DockerLaunch.ExportAssets(image), cancellationToken: cancellationToken);
        await processes.RunAsync(["tar", "-xf", "-", "-C", assets], archive.StandardOutput, cancellationToken);
        return assets;
    }
}

public static class DockerLaunch
{
    public const string Image = "campfire-reference:app";
    public const string SecretKeyBase = "isolated-benchmark-fixture-key";

    public static IReadOnlyList<string> ExportAssets(string image) =>
    [
        "docker", "run", "--rm", "--entrypoint", "", image,
        "tar", "-C", "/rails/public/assets", "-cf", "-", ".",
    ];

    public static IReadOnlyList<string> Redis(string container, string network) =>
    [
        "docker", "run", "-d", "--name", container, "--network", network, "redis:7-alpine",
    ];

    public static IReadOnlyList<string> HttpServer(
        string container,
        string network,
        string cpus,
        int port,
        string source,
        string storage,
        string temp,
        string log,
        string assets,
        string redisUrl,
        string image)
    {
        var arguments = new List<string>
        {
            "docker", "run", "-d", "--name", container, "--entrypoint", "",
            "--network", network, "--cpuset-cpus", cpus, "-p", $"127.0.0.1:{port}:3000",
        };
        AddMounts(arguments,
            (source, "/rails"),
            (storage, "/rails/storage"),
            (temp, "/rails/tmp"),
            (log, "/rails/log"),
            (assets, "/rails/public/assets"));
        AddEnvironment(arguments, HttpEnvironment(redisUrl));
        arguments.Add(image);
        arguments.Add("bundle");
        arguments.Add("exec");
        arguments.Add("puma");
        arguments.Add("-C");
        arguments.Add("config/puma.rb");
        return arguments;
    }

    public static IReadOnlyList<string> HotPathProbe(
        string cpus,
        string source,
        string storage,
        string temp,
        string log,
        string assets,
        string bench,
        string labels,
        string image)
    {
        var arguments = new List<string>
        {
            "docker", "run", "--rm", "--entrypoint", "", "--cpuset-cpus", cpus,
        };
        AddMounts(arguments,
            (source, "/rails"),
            (storage, "/rails/storage"),
            (temp, "/rails/tmp"),
            (log, "/rails/log"),
            (assets, "/rails/public/assets"),
            (bench, "/bench"),
            (labels, "/bench-labels.json"));
        AddEnvironment(arguments, HotPathEnvironment());
        arguments.Add(image);
        arguments.Add("bundle");
        arguments.Add("exec");
        arguments.Add("ruby");
        arguments.Add("-r");
        arguments.Add("/rails/config/environment.rb");
        arguments.Add("/bench/message_hot_paths.rb");
        return arguments;
    }

    public static IEnumerable<(string Name, string Value)> HttpEnvironment(string redisUrl)
    {
        foreach (var pair in IsolatedEnvironment())
            yield return pair;
        yield return ("WEB_CONCURRENCY", "1");
        yield return ("JOB_CONCURRENCY", "1");
        yield return ("RAILS_MAX_THREADS", "5");
        yield return ("REDIS_URL", redisUrl);
    }

    public static IEnumerable<(string Name, string Value)> HotPathEnvironment()
    {
        foreach (var pair in IsolatedEnvironment())
            yield return pair;
        yield return ("BENCH_LABELS", "/bench-labels.json");
    }

    private static IEnumerable<(string Name, string Value)> IsolatedEnvironment()
    {
        yield return ("RAILS_ENV", "production");
        yield return ("SECRET_KEY_BASE", SecretKeyBase);
        yield return ("DISABLE_SSL", "true");
        yield return ("SKIP_TELEMETRY", "true");
        yield return ("RAILS_LOG_LEVEL", "fatal");
    }

    private static void AddMounts(List<string> arguments, params (string Host, string Target)[] mounts)
    {
        foreach (var (host, target) in mounts)
        {
            arguments.Add("-v");
            arguments.Add($"{host}:{target}");
        }
    }

    private static void AddEnvironment(List<string> arguments, IEnumerable<(string Name, string Value)> values)
    {
        foreach (var (name, value) in values)
        {
            arguments.Add("-e");
            arguments.Add($"{name}={value}");
        }
    }
}
