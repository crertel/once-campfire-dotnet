namespace Campfire.Bench;

public static class AspNetLaunch
{
    public static string Project(string root) =>
        Path.Combine(root, "src", "Campfire.Web", "Campfire.Web.csproj");

    public static IReadOnlyList<string> Build(string root) =>
    [
        "dotnet", "build", Project(root), "--nologo", "-v", "q",
    ];

    public static IReadOnlyList<string> Seed(string root, string output) =>
    [
        "dotnet", "run", "--project", Project(root), "--no-launch-profile", "--no-build",
        "--", "seed", "--output", output,
    ];

    public static IReadOnlyList<string> Server(string root, string cpus) =>
    [
        "setsid", "--wait",
        "taskset", "-c", cpus,
        "dotnet", "run", "--project", Project(root), "--no-launch-profile", "--no-build",
    ];

    public static IReadOnlyDictionary<string, string> ServerEnvironment(int port, string database) =>
        new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["CAMPFIRE_DB"] = database,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0",
        };
}
