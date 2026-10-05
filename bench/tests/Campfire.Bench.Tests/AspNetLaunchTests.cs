namespace Campfire.Bench.Tests;

public class AspNetLaunchTests
{
    [Fact]
    public void Seed_and_server_commands_run_this_checkout()
    {
        const string root = "/repo";
        const string project = "/repo/src/Campfire.Web/Campfire.Web.csproj";

        Assert.Equal(["dotnet", "build", project, "--nologo", "-v", "q"], AspNetLaunch.Build(root));
        Assert.Equal(
        [
            "dotnet", "run", "--project", project, "--no-launch-profile", "--no-build",
            "--", "seed", "--output", "/tmp/asp",
        ], AspNetLaunch.Seed(root, "/tmp/asp"));
        Assert.Equal(
        [
            "setsid", "--wait",
            "taskset", "-c", "8-11",
            "dotnet", "run", "--project", project, "--no-launch-profile", "--no-build",
        ], AspNetLaunch.Server(root, "8-11"));
    }

    [Fact]
    public void Server_environment_points_kestrel_at_the_seeded_database()
    {
        var environment = AspNetLaunch.ServerEnvironment(3000, "/tmp/asp/campfire.sqlite");

        Assert.Equal("http://127.0.0.1:3000", environment["ASPNETCORE_URLS"]);
        Assert.Equal("Production", environment["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("/tmp/asp/campfire.sqlite", environment["CAMPFIRE_DB"]);
        Assert.Equal("1", environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("1", environment["DOTNET_NOLOGO"]);
        Assert.Equal("1", environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]);
        Assert.Equal("0", environment["DOTNET_MULTILEVEL_LOOKUP"]);
    }

    [Fact]
    public void Server_flag_selects_rails_asp_or_both()
    {
        var both = HttpCompareOptions.Parse(["--baseline", "/tmp/baseline", "--seed", "/tmp/seed"], "/tmp/repo");
        Assert.Equal("rails", both.BeforeServer);
        Assert.Equal("asp", both.AfterServer);

        var rails = HttpCompareOptions.Parse(
            ["--baseline", "/tmp/baseline", "--seed", "/tmp/seed", "--server", "rails"],
            "/tmp/repo");
        Assert.Equal("rails", rails.BeforeServer);
        Assert.Equal("rails", rails.AfterServer);
        Assert.True(rails.RunsRails);
        Assert.False(rails.RunsAsp);

        var asp = HttpCompareOptions.Parse(["--baseline", "/tmp/baseline", "--server", "asp"], "/tmp/repo");
        Assert.Equal("asp", asp.BeforeServer);
        Assert.Equal("asp", asp.AfterServer);
        Assert.Null(asp.Seed);
        Assert.False(asp.RunsRails);
        Assert.True(asp.RunsAsp);
        Assert.Equal("asp", asp.ServerFor("before"));
        Assert.Equal("asp", asp.ServerFor("after"));

        var error = Assert.Throws<OptionException>(() => HttpCompareOptions.Parse(
            ["--baseline", "/tmp/baseline", "--seed", "/tmp/seed", "--server", "puma"],
            "/tmp/repo"));
        Assert.Contains("--server must be rails, asp, or both", error.Message, StringComparison.Ordinal);
    }
}
