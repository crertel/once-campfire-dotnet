using System.Net;
using System.Text.RegularExpressions;
using Campfire.Core;
using Campfire.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Campfire.Tests;

public sealed class RecordingRealtime : ICampfireRealtime
{
    public List<LiveMessage> Messages { get; } = [];
    public List<LiveRemoval> Removals { get; } = [];
    public List<(long UserId, LiveUnread Unread)> Unreads { get; } = [];
    public List<LivePresence> Presences { get; } = [];
    public int SignOuts { get; private set; }
    public bool ThrowOnSignOut { get; set; }

    public Task MessageAsync(LiveMessage message, CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task RemovedAsync(LiveRemoval removal, CancellationToken cancellationToken = default)
    {
        Removals.Add(removal);
        return Task.CompletedTask;
    }

    public Task UnreadAsync(long userId, LiveUnread unread, CancellationToken cancellationToken = default)
    {
        Unreads.Add((userId, unread));
        return Task.CompletedTask;
    }

    public Task PresenceAsync(LivePresence presence, CancellationToken cancellationToken = default)
    {
        Presences.Add(presence);
        return Task.CompletedTask;
    }

    public Task SignedOutAsync(long userId, CancellationToken cancellationToken = default)
    {
        SignOuts++;
        if (ThrowOnSignOut)
            throw new InvalidOperationException("cable down");
        return Task.CompletedTask;
    }
}

public sealed class AppWorld : IDisposable
{
    public string Path { get; }
    public CampfireDb Db { get; }
    public ManualTime Time { get; }
    public RecordingRealtime Realtime { get; }
    public CampfireApp App { get; }

    public AppWorld()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"campfire-{Guid.NewGuid():n}.sqlite");
        var options = new DbContextOptionsBuilder<CampfireDb>().UseSqlite(Seeder.Connection(Path)).Options;
        Db = new CampfireDb(options);
        Db.Database.EnsureCreated();
        Time = new ManualTime();
        Realtime = new RecordingRealtime();
        App = new CampfireApp(Db, Time, Realtime);
    }

    public void Dispose()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(Path))
            File.Delete(Path);
    }
}

public class CampfireFactory : WebApplicationFactory<Program>
{
    public string DatabasePath { get; }

    public CampfireFactory(string? databasePath = null)
    {
        DatabasePath = databasePath ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"campfire-host-{Guid.NewGuid():n}", "db", "campfire.sqlite");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DatabasePath)!);
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("Campfire:Database", DatabasePath);
        builder.UseEnvironment("Development");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
    }
}

public static partial class HttpFlow
{
    public static HttpClient Client(CampfireFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public static async Task<Dictionary<string, string>> FirstRunAsync(HttpClient client, string name = "David", string email = "david@37signals.com", string password = "secret123456")
    {
        var cookies = new Dictionary<string, string>();
        using var page = await client.GetAsync("/first_run");
        Collect(page, cookies);
        var html = await page.Content.ReadAsStringAsync();
        using var post = new HttpRequestMessage(HttpMethod.Post, "/first_run")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user[name]"] = name,
                ["user[email_address]"] = email,
                ["user[password]"] = password,
                ["authenticity_token"] = Token(html),
            }),
        };
        Add(post, cookies);
        using var created = await client.SendAsync(post);
        Collect(created, cookies);
        return cookies;
    }

    public static async Task<Dictionary<string, string>> LoginAsync(HttpClient client, string email, string password)
    {
        var cookies = new Dictionary<string, string>();
        using var page = await client.GetAsync("/session/new");
        Collect(page, cookies);
        var html = await page.Content.ReadAsStringAsync();
        using var post = new HttpRequestMessage(HttpMethod.Post, "/session")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email_address"] = email,
                ["password"] = password,
                ["authenticity_token"] = Token(html),
            }),
        };
        post.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        post.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        Add(post, cookies);
        using var created = await client.SendAsync(post);
        Collect(created, cookies);
        if (created.StatusCode != HttpStatusCode.Found)
            throw new InvalidOperationException($"login HTTP {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        return cookies;
    }

    public static void Collect(HttpResponseMessage response, Dictionary<string, string> cookies)
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

    public static void Add(HttpRequestMessage request, Dictionary<string, string> cookies)
    {
        if (cookies.Count == 0)
            return;
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
    }

    public static string Token(string html)
    {
        var match = Csrf().Match(html);
        if (!match.Success)
            throw new InvalidOperationException("page has no authenticity token");
        var value = match.Groups[1].Success && match.Groups[1].Length > 0 ? match.Groups[1].Value : match.Groups[2].Value;
        return WebUtility.HtmlDecode(value);
    }

    [GeneratedRegex("name=\"authenticity_token\"(?:[^>]*?)value=\"([^\"]*)\"|value=\"([^\"]*)\"(?:[^>]*?)name=\"authenticity_token\"", RegexOptions.CultureInvariant)]
    private static partial Regex Csrf();
}
