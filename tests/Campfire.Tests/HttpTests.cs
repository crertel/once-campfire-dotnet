using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Campfire.Bench;
using Campfire.Core;
using Campfire.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;

namespace Campfire.Tests;

public sealed class HttpTests
{
    [Fact]
    public async Task Empty_campfire_serves_the_setup_form_and_session_new_redirects()
    {
        using var factory = new CampfireFactory();
        using var client = HttpFlow.Client(factory);
        using var home = await client.GetAsync("/");
        var html = await home.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Set up Campfire", html);
        Assert.Contains("user[name]", html);
        Assert.Contains("user[email_address]", html);
        Assert.Contains("user[password]", html);
        Assert.Contains("name=\"authenticity_token\"", html);

        using var signIn = await client.GetAsync("/session/new");
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Contains("/first_run", signIn.Headers.Location?.OriginalString);

        var cookies = new Dictionary<string, string>();
        HttpFlow.Collect(home, cookies);
        var fields = FormFields(html);
        fields["user[name]"] = "David";
        fields["user[email_address]"] = "david@37signals.com";
        fields["user[password]"] = "secret123456";
        using var created = new HttpRequestMessage(HttpMethod.Post, FormAction(html))
        {
            Content = new FormUrlEncodedContent(fields),
        };
        HttpFlow.Add(created, cookies);
        using var started = await client.SendAsync(created);
        Assert.Equal(HttpStatusCode.Redirect, started.StatusCode);
        HttpFlow.Collect(started, cookies);
        Assert.False(string.IsNullOrEmpty(cookies["session_token"]));

        using var after = new HttpRequestMessage(HttpMethod.Get, started.Headers.Location);
        HttpFlow.Add(after, cookies);
        using var next = await client.SendAsync(after);
        Assert.Equal(HttpStatusCode.Redirect, next.StatusCode);
        using var roomRequest = new HttpRequestMessage(HttpMethod.Get, next.Headers.Location);
        HttpFlow.Add(roomRequest, cookies);
        using var room = await client.SendAsync(roomRequest);
        var roomHtml = await room.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, room.StatusCode);
        Assert.Contains("All Talk", roomHtml);
        Assert.Contains("/js/campfire.js", roomHtml);
        Assert.Contains("/hubs/campfire", await ScriptAsync(client, roomHtml, "/js/campfire.js"));
    }

    [Fact]
    public async Task Avatar_rejects_html_and_serves_only_an_image()
    {
        using var factory = new CampfireFactory();
        using var client = HttpFlow.Client(factory);
        var cookies = await HttpFlow.FirstRunAsync(client);
        using var profile = new HttpRequestMessage(HttpMethod.Get, "/users/me/profile");
        HttpFlow.Add(profile, cookies);
        using var profileResponse = await client.SendAsync(profile);
        var avatarUrl = FormAction(await profileResponse.Content.ReadAsStringAsync(), "avatar");
        Assert.Contains("/avatar", avatarUrl);

        using var html = new MultipartFormDataContent();
        var script = new ByteArrayContent("<script>alert(1)</script>"u8.ToArray());
        script.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
        html.Add(script, "avatar", "evil.html");
        using var rejected = new HttpRequestMessage(HttpMethod.Post, avatarUrl) { Content = html };
        HttpFlow.Add(rejected, cookies);
        using var rejectedResponse = await client.SendAsync(rejected);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejectedResponse.StatusCode);
        using var missing = await client.GetAsync(avatarUrl);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        using var image = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
        image.Add(file, "avatar", "avatar.html");
        using var uploaded = new HttpRequestMessage(HttpMethod.Post, avatarUrl) { Content = image };
        HttpFlow.Add(uploaded, cookies);
        using var uploadedResponse = await client.SendAsync(uploaded);
        Assert.Equal(HttpStatusCode.Redirect, uploadedResponse.StatusCode);

        using var served = await client.GetAsync(avatarUrl);
        var body = await served.Content.ReadAsByteArrayAsync();
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(png, body);
        Assert.DoesNotContain("text/html", served.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_sets_a_session_cookie_and_logout_removes_it()
    {
        using var factory = new CampfireFactory();
        using var client = HttpFlow.Client(factory);
        var cookies = await HttpFlow.FirstRunAsync(client);
        Assert.True(cookies.ContainsKey("session_token"));

        using var signOut = new HttpRequestMessage(HttpMethod.Delete, "/session");
        HttpFlow.Add(signOut, cookies);
        using var signedOut = await client.SendAsync(signOut);
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        HttpFlow.Collect(signedOut, cookies);
        Assert.False(cookies.TryGetValue("session_token", out var token) && token.Length > 0);

        var again = await HttpFlow.LoginAsync(client, "david@37signals.com", "secret123456");
        Assert.True(again.ContainsKey("session_token"));

        using var rejectedPage = await client.GetAsync("/session/new");
        var jar = new Dictionary<string, string>();
        HttpFlow.Collect(rejectedPage, jar);
        using var rejected = new HttpRequestMessage(HttpMethod.Post, "/session")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email_address"] = "david@37signals.com",
                ["password"] = "wrong",
                ["authenticity_token"] = HttpFlow.Token(await rejectedPage.Content.ReadAsStringAsync()),
            }),
        };
        rejected.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        rejected.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        HttpFlow.Add(rejected, jar);
        using var unauthorized = await client.SendAsync(rejected);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.DoesNotContain(unauthorized.Headers, header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) && header.Value.Any(value => value.StartsWith("session_token=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Old_firefox_is_asked_to_upgrade_and_safari_17_2_is_not()
    {
        using var factory = new CampfireFactory();
        using var client = HttpFlow.Client(factory);
        using var blocked = new HttpRequestMessage(HttpMethod.Get, "/session/new");
        blocked.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/114.0");
        using var blockedResponse = await client.SendAsync(blocked);
        Assert.Contains("Upgrade to a supported web browser", await blockedResponse.Content.ReadAsStringAsync());

        using var allowed = new HttpRequestMessage(HttpMethod.Get, "/");
        allowed.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15");
        using var allowedResponse = await client.SendAsync(allowed);
        Assert.DoesNotContain("Upgrade to a supported web browser", await allowedResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_empty_room_is_204_and_a_before_page_of_a_used_room_is_200()
    {
        using var factory = new CampfireFactory();
        using var client = HttpFlow.Client(factory);
        var cookies = await HttpFlow.FirstRunAsync(client);
        using var home = new HttpRequestMessage(HttpMethod.Get, "/");
        HttpFlow.Add(home, cookies);
        using var redirected = await client.SendAsync(home);
        var roomPath = redirected.Headers.Location!.OriginalString;

        using var empty = new HttpRequestMessage(HttpMethod.Get, roomPath + "/messages");
        HttpFlow.Add(empty, cookies);
        using var emptyResponse = await client.SendAsync(empty);
        Assert.Equal(HttpStatusCode.NoContent, emptyResponse.StatusCode);

        using var create = new HttpRequestMessage(HttpMethod.Post, roomPath + "/messages")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["body"] = "hello room" }),
        };
        HttpFlow.Add(create, cookies);
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        using var room = new HttpRequestMessage(HttpMethod.Get, roomPath);
        HttpFlow.Add(room, cookies);
        using var roomResponse = await client.SendAsync(room);
        var roomHtml = await roomResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, roomResponse.StatusCode);
        Assert.Contains("All Talk", roomHtml);
        Assert.Contains("id=\"composer\"", roomHtml);
        Assert.Contains("hello room", roomHtml);
        Assert.Contains("room-live", roomHtml);

        using var page = new HttpRequestMessage(HttpMethod.Get, roomPath + "/messages?before=999999");
        HttpFlow.Add(page, cookies);
        using var pageResponse = await client.SendAsync(page);
        Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
    }

    [Fact]
    public async Task The_bench_client_measures_only_successful_responses()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"campfire-bench-{Guid.NewGuid():n}");
        await Seeder.WriteAsync(directory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
        var port = FreePort();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Campfire:Database"] = Path.Combine(directory, "campfire.sqlite"),
        });
        var app = CampfireWeb.Build(builder);
        await app.StartAsync();
        try
        {
            var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var bench = new BenchmarkHttpClient(baseUrl);
            Assert.True(await bench.ReadyAsync());
            var cookie = await bench.LoginAsync(Seeder.Email, Seeder.Password);
            using var labels = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "labels.json")));
            using var client = new HttpClient();
            foreach (var (name, path) in Workloads.All(labels.RootElement))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), path));
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                using var response = await client.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{name} {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static Dictionary<string, string> FormFields(string html)
    {
        var fields = new Dictionary<string, string>();
        foreach (System.Text.RegularExpressions.Match input in System.Text.RegularExpressions.Regex.Matches(html, "<input\\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var name = Attribute(input.Value, "name");
            if (string.IsNullOrEmpty(name))
                continue;
            fields[name] = System.Net.WebUtility.HtmlDecode(Attribute(input.Value, "value") ?? "");
        }
        return fields;
    }

    private static string FormAction(string html, string? inputName = null)
    {
        var pattern = inputName is null
            ? "<form\\b[^>]*\\baction=\"([^\"]*)\""
            : "<form\\b[^>]*\\baction=\"([^\"]*" + System.Text.RegularExpressions.Regex.Escape(inputName) + "[^\"]*)\"";
        var match = System.Text.RegularExpressions.Regex.Match(html, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
            throw new InvalidOperationException("form has no action");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string? Attribute(string tag, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(tag, name + "=\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<string> ScriptAsync(HttpClient client, string html, string src)
    {
        Assert.Contains(src, html);
        using var response = await client.GetAsync(src);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public sealed class LiveTests
{
    [Fact]
    public async Task Two_clients_see_a_message_an_unread_change_and_presence()
    {
        using var factory = new CampfireFactory();
        using var client = HttpFlow.Client(factory);
        var david = await HttpFlow.FirstRunAsync(client, "David", "david@37signals.com", "secret123456");
        using var account = new HttpRequestMessage(HttpMethod.Get, "/account");
        HttpFlow.Add(account, david);
        using var accountResponse = await client.SendAsync(account);
        var accountHtml = await accountResponse.Content.ReadAsStringAsync();
        var code = System.Text.RegularExpressions.Regex.Match(accountHtml, "id=\"join-code\">([^<]+)<").Groups[1].Value;

        using var joiner = HttpFlow.Client(factory);
        var jasonCookies = new Dictionary<string, string>();
        using var joinPage = await joiner.GetAsync($"/join/{code}");
        HttpFlow.Collect(joinPage, jasonCookies);
        using var join = new HttpRequestMessage(HttpMethod.Post, $"/join/{code}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["name"] = "Jason",
                ["email_address"] = "jason@37signals.com",
                ["password"] = "secret123456",
                ["authenticity_token"] = HttpFlow.Token(await joinPage.Content.ReadAsStringAsync()),
            }),
        };
        HttpFlow.Add(join, jasonCookies);
        using var joined = await joiner.SendAsync(join);
        HttpFlow.Collect(joined, jasonCookies);

        using var home = new HttpRequestMessage(HttpMethod.Get, "/");
        HttpFlow.Add(home, david);
        using var redirected = await client.SendAsync(home);
        var roomId = long.Parse(redirected.Headers.Location!.OriginalString.Split('/').Last());

        await using var davidHub = Hub(factory, david);
        await using var jasonHub = Hub(factory, jasonCookies);
        var message = new TaskCompletionSource<LiveMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unread = new TaskCompletionSource<LiveUnread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var presence = new TaskCompletionSource<LivePresence>(TaskCreationOptions.RunContinuationsAsynchronously);
        jasonHub.On<LiveMessage>("Message", item => message.TrySetResult(item));
        jasonHub.On<LiveUnread>("Unread", item => unread.TrySetResult(item));
        davidHub.On<LivePresence>("Presence", item => presence.TrySetResult(item));

        await davidHub.StartAsync();
        await jasonHub.StartAsync();
        await davidHub.InvokeAsync("JoinRoom", roomId);
        await jasonHub.InvokeAsync("JoinRoom", roomId);
        var seen = await presence.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(roomId, seen.RoomId);

        using var post = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{roomId}/messages")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["body"] = "live hello" }),
        };
        HttpFlow.Add(post, david);
        using var posted = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);

        var delivered = await message.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var unreadEvent = await unread.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("live hello", delivered.Text);
        Assert.Equal(roomId, unreadEvent.RoomId);
        Assert.Contains("live hello", delivered.Html);
    }

    private static HubConnection Hub(CampfireFactory factory, Dictionary<string, string> cookies) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/campfire"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Headers.Add("Cookie", string.Join("; ", cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
}
