using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Campfire.Bench;

public sealed record LatencyMs(double P50, double P95, double P99);

public sealed record Measurement(
    string Path,
    int Concurrency,
    bool Gzip,
    double Seconds,
    double RequestsPerSecond,
    int Ok,
    IReadOnlyDictionary<string, int> Statuses,
    int Errors,
    long AverageBytes,
    LatencyMs Latency)
{
    public JsonObject ToJson()
    {
        var statuses = new JsonObject();
        foreach (var (status, count) in Statuses)
            statuses[status] = count;

        return new JsonObject
        {
            ["path"] = Path,
            ["conc"] = Concurrency,
            ["gzip"] = Gzip,
            ["secs"] = Seconds,
            ["rps"] = RequestsPerSecond,
            ["ok"] = Ok,
            ["statuses"] = statuses,
            ["errors"] = Errors,
            ["avg_bytes"] = AverageBytes,
            ["latency_ms"] = new JsonObject
            {
                ["p50"] = Latency.P50,
                ["p95"] = Latency.P95,
                ["p99"] = Latency.P99,
            },
        };
    }
}

public sealed partial class BenchmarkHttpClient
{
    private static readonly Regex CsrfToken = CsrfPattern();

    private readonly Uri _base;

    public BenchmarkHttpClient(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Base URL must be an absolute http(s) URI.", nameof(baseUrl));
        _base = uri;
    }

    public async Task<bool> ReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(Url("/up"), cancellationToken);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return false;
        }
    }

    public async Task<string> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var cookies = new Dictionary<string, string>();

        using var signIn = SignInRequest(cookies);
        using var page = await client.SendAsync(signIn, cancellationToken);
        if (page.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"sign-in page: HTTP {(int)page.StatusCode}");
        MergeCookies(cookies, page);
        var html = await page.Content.ReadAsStringAsync(cancellationToken);
        var token = CsrfToken.Match(html) is { Success: true } match
            ? WebUtility.HtmlDecode(match.Groups[1].Value)
            : null;
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("sign-in page has no CSRF token");

        using var login = LoginRequest(cookies, email, password, token);
        using var posted = await client.SendAsync(login, cancellationToken);
        MergeCookies(cookies, posted);
        if (posted.StatusCode != HttpStatusCode.Found || !cookies.ContainsKey("session_token"))
            throw new InvalidOperationException($"login failed: HTTP {(int)posted.StatusCode}");

        return CookieHeader(cookies);
    }

    public async Task<Measurement> MeasureAsync(
        string path,
        string cookie,
        int concurrency,
        double durationSeconds,
        CancellationToken cancellationToken = default)
    {
        var start = MonotonicClock.Seconds();
        var deadline = start + durationSeconds;
        var workers = Enumerable.Range(0, concurrency)
            .Select(_ => MeasureWorkerAsync(path, cookie, deadline, cancellationToken))
            .ToArray();
        var samples = await Task.WhenAll(workers);
        var elapsed = MonotonicClock.Seconds() - start;

        var latencies = samples.SelectMany(sample => sample.Latencies).ToArray();
        var statuses = new Dictionary<string, int>();
        foreach (var sample in samples)
        {
            foreach (var (status, count) in sample.Statuses)
                statuses[status] = statuses.GetValueOrDefault(status) + count;
        }

        var errors = samples.Sum(sample => sample.Errors);
        if (errors != 0 || statuses.Count != 1 || !statuses.ContainsKey("200"))
            throw new InvalidOperationException($"{path}: HTTP statuses {FormatStatuses(statuses)}, {errors} transport errors");

        var bytes = samples.Sum(sample => sample.Bytes);
        return new Measurement(
            path,
            concurrency,
            Gzip: false,
            elapsed,
            latencies.Length / elapsed,
            latencies.Length,
            statuses,
            errors,
            bytes / latencies.Length,
            new LatencyMs(
                Statistics.Percentile(latencies, 0.50),
                Statistics.Percentile(latencies, 0.95),
                Statistics.Percentile(latencies, 0.99)));
    }

    private async Task<WorkerSample> MeasureWorkerAsync(
        string path,
        string cookie,
        double deadline,
        CancellationToken cancellationToken)
    {
        var result = new WorkerSample();
        var url = Url(path);
        while (MonotonicClock.Seconds() < deadline)
        {
            try
            {
                using var client = CreateClient();
                while (MonotonicClock.Seconds() < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = MonotonicClock.Seconds();
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("Cookie", cookie);
                    request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    result.Latencies.Add((MonotonicClock.Seconds() - requested) * 1000);
                    var status = ((int)response.StatusCode).ToString();
                    result.Statuses[status] = result.Statuses.GetValueOrDefault(status) + 1;
                    result.Bytes += body.Length;
                }
            }
            catch (Exception ex) when (IsTransport(ex) && !cancellationToken.IsCancellationRequested)
            {
                result.Errors++;
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }

        return result;
    }

    private HttpRequestMessage SignInRequest(Dictionary<string, string> cookies)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url("/session/new"));
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        AddCookie(request, cookies);
        return request;
    }

    private HttpRequestMessage LoginRequest(Dictionary<string, string> cookies, string email, string password, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url("/session"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email_address"] = email,
                ["password"] = password,
                ["authenticity_token"] = token,
            }),
        };
        request.Headers.TryAddWithoutValidation("Origin", _base.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        AddCookie(request, cookies);
        return request;
    }

    private Uri Url(string pathAndQuery) => new(_base, pathAndQuery);

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            UseCookies = false,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(5),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    private static void AddCookie(HttpRequestMessage request, Dictionary<string, string> cookies)
    {
        var header = CookieHeader(cookies);
        if (header.Length > 0)
            request.Headers.TryAddWithoutValidation("Cookie", header);
    }

    internal static void MergeCookies(Dictionary<string, string> cookies, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
            return;

        foreach (var header in headers)
        {
            var pair = header.Split(';', 2)[0];
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;
            cookies[pair[..separator]] = pair[(separator + 1)..];
        }
    }

    internal static string CookieHeader(Dictionary<string, string> cookies) =>
        string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}"));

    private static bool IsTransport(Exception exception) =>
        exception is HttpRequestException or IOException or SocketException or TaskCanceledException;

    private static string FormatStatuses(Dictionary<string, int> statuses) =>
        statuses.Count == 0 ? "{}" : string.Join(", ", statuses.Select(status => $"{status.Key}={status.Value}"));

    [GeneratedRegex("<meta name=\"csrf-token\" content=\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex CsrfPattern();

    private sealed class WorkerSample
    {
        public List<double> Latencies { get; } = [];
        public Dictionary<string, int> Statuses { get; } = [];
        public long Bytes { get; set; }
        public int Errors { get; set; }
    }
}
