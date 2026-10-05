using System.Net;
using System.Net.Sockets;

namespace Campfire.Bench.Tests;

public class HttpClientTests
{
    [Fact]
    public void Cookies_keep_each_set_cookie_pair()
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session_token=sess; Path=/; HttpOnly");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "other=1; Path=/");
        var cookies = new Dictionary<string, string>();

        BenchmarkHttpClient.MergeCookies(cookies, response);

        Assert.Equal("session_token=sess; other=1", BenchmarkHttpClient.CookieHeader(cookies));
    }

    [Fact]
    public async Task Ready_is_true_only_when_up_returns_200()
    {
        await using var server = await FakeCampfire.StartAsync();
        var client = new BenchmarkHttpClient(server.BaseUrl);
        Assert.True(await client.ReadyAsync());

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.False(await new BenchmarkHttpClient($"http://127.0.0.1:{port}").ReadyAsync());
    }

    [Fact]
    public async Task Login_posts_the_decoded_csrf_token_and_returns_the_session_cookie()
    {
        await using var server = await FakeCampfire.StartAsync();
        var client = new BenchmarkHttpClient(server.BaseUrl);

        var cookie = await client.LoginAsync("david@example.com", "secret");

        Assert.Contains("pre_session=abc", cookie, StringComparison.Ordinal);
        Assert.Contains("session_token=sess", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_fails_without_a_csrf_token_or_a_session_cookie()
    {
        await using var missing = await FakeCampfire.StartAsync(FakeCampfireMode.MissingCsrf);
        var client = new BenchmarkHttpClient(missing.BaseUrl);
        var missingToken = await Assert.ThrowsAsync<InvalidOperationException>(() => client.LoginAsync("david@example.com", "secret"));
        Assert.Equal("sign-in page has no CSRF token", missingToken.Message);

        await using var server = await FakeCampfire.StartAsync();
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BenchmarkHttpClient(server.BaseUrl).LoginAsync("david@example.com", "nope"));
        Assert.Equal("login failed: HTTP 401", rejected.Message);
    }

    [Fact]
    public async Task Measure_reads_uncompressed_bodies_over_keep_alive()
    {
        await using var server = await FakeCampfire.StartAsync();
        var client = new BenchmarkHttpClient(server.BaseUrl);
        var cookie = await client.LoginAsync("david@example.com", "secret");

        var measurement = await client.MeasureAsync("/rooms/7", cookie, concurrency: 2, durationSeconds: 0.2);

        Assert.True(measurement.Ok > 1);
        Assert.Equal(0, measurement.Errors);
        Assert.False(measurement.Gzip);
        Assert.Equal(5, measurement.AverageBytes);
        Assert.Equal(measurement.Ok, measurement.Statuses["200"]);
        Assert.True(measurement.RequestsPerSecond > 0);
        Assert.True(measurement.Latency.P50 <= measurement.Latency.P95);
        Assert.True(measurement.Latency.P95 <= measurement.Latency.P99);
        Assert.All(server.MeasuredEncodings, encoding => Assert.Contains("identity", encoding, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Measure_rejects_anything_but_http_200()
    {
        await using var server = await FakeCampfire.StartAsync(FakeCampfireMode.ServerError);
        var client = new BenchmarkHttpClient(server.BaseUrl);
        var cookie = await client.LoginAsync("david@example.com", "secret");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.MeasureAsync("/rooms/7", cookie, concurrency: 1, durationSeconds: 0.05));

        Assert.Contains("/rooms/7: HTTP statuses", failure.Message, StringComparison.Ordinal);
        Assert.Contains("transport errors", failure.Message, StringComparison.Ordinal);
    }
}
