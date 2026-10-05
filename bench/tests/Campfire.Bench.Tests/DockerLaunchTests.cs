namespace Campfire.Bench.Tests;

public class DockerLaunchTests
{
    [Fact]
    public void Http_server_command_matches_the_isolated_puma_launch()
    {
        var actual = DockerLaunch.HttpServer(
            "app",
            "net",
            "8-11",
            3000,
            "/src",
            "/storage",
            "/tmp",
            "/log",
            "/assets",
            "redis://net-redis:6379/0",
            "campfire-reference:app");

        Assert.Equal(
        [
            "docker", "run", "-d", "--name", "app", "--entrypoint", "",
            "--network", "net", "--cpuset-cpus", "8-11", "-p", "127.0.0.1:3000:3000",
            "-v", "/src:/rails",
            "-v", "/storage:/rails/storage",
            "-v", "/tmp:/rails/tmp",
            "-v", "/log:/rails/log",
            "-v", "/assets:/rails/public/assets",
            "-e", "RAILS_ENV=production",
            "-e", "SECRET_KEY_BASE=isolated-benchmark-fixture-key",
            "-e", "DISABLE_SSL=true",
            "-e", "SKIP_TELEMETRY=true",
            "-e", "RAILS_LOG_LEVEL=fatal",
            "-e", "WEB_CONCURRENCY=1",
            "-e", "JOB_CONCURRENCY=1",
            "-e", "RAILS_MAX_THREADS=5",
            "-e", "REDIS_URL=redis://net-redis:6379/0",
            "campfire-reference:app", "bundle", "exec", "puma", "-C", "config/puma.rb",
        ], actual);
    }

    [Fact]
    public void Hot_path_command_runs_the_rails_probe_with_labels_mounted()
    {
        var actual = DockerLaunch.HotPathProbe(
            "8-11", "/src", "/storage", "/tmp", "/log", "/assets", "/bench", "/labels.json", "campfire-reference:app");

        Assert.Equal(
        [
            "docker", "run", "--rm", "--entrypoint", "", "--cpuset-cpus", "8-11",
            "-v", "/src:/rails",
            "-v", "/storage:/rails/storage",
            "-v", "/tmp:/rails/tmp",
            "-v", "/log:/rails/log",
            "-v", "/assets:/rails/public/assets",
            "-v", "/bench:/bench",
            "-v", "/labels.json:/bench-labels.json",
            "-e", "RAILS_ENV=production",
            "-e", "SECRET_KEY_BASE=isolated-benchmark-fixture-key",
            "-e", "DISABLE_SSL=true",
            "-e", "SKIP_TELEMETRY=true",
            "-e", "RAILS_LOG_LEVEL=fatal",
            "-e", "BENCH_LABELS=/bench-labels.json",
            "campfire-reference:app", "bundle", "exec", "ruby", "-r", "/rails/config/environment.rb", "/bench/message_hot_paths.rb",
        ], actual);
    }
}
