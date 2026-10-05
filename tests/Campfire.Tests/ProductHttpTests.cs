using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Campfire.Tests;

public sealed class ProductHttpTests
{
    [Fact]
    public async Task Rooms_can_be_created_edited_and_kept_private()
    {
        using var factory = new CampfireFactory();
        using var client = HttpFlow.Client(factory);
        var david = await HttpFlow.FirstRunAsync(client);
        var openId = await CreateRoomAsync(client, david, "opens", "Design");
        using var open = await GetAsync(client, david, $"/rooms/{openId}");
        var openHtml = await open.Content.ReadAsStringAsync();
        Assert.Contains("Design", openHtml);
        Assert.Contains($"/rooms/opens/{openId}/edit", openHtml);
        Assert.Contains("name=\"csrf-token\"", openHtml);
        Assert.Contains("data-unfurl", openHtml);
        Assert.Contains("notification-bell-mentions.svg", openHtml);

        var edit = await GetAsync(client, david, $"/rooms/opens/{openId}/edit");
        var editHtml = await edit.Content.ReadAsStringAsync();
        HttpFlow.Collect(edit, david);
        using var rename = new HttpRequestMessage(HttpMethod.Post, $"/rooms/opens/{openId}/edit")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["room[name]"] = "Design critique",
                ["authenticity_token"] = HttpFlow.Token(editHtml),
            }),
        };
        HttpFlow.Add(rename, david);
        using var renamed = await client.SendAsync(rename);
        Assert.Equal(HttpStatusCode.Redirect, renamed.StatusCode);

        var code = await JoinCodeAsync(client, david);
        using var joiner = HttpFlow.Client(factory);
        var jason = await JoinAsync(joiner, code, "Jason", "jason@37signals.com");
        using var shared = await GetAsync(joiner, jason, $"/rooms/{openId}");
        Assert.Equal(HttpStatusCode.OK, shared.StatusCode);

        var closedId = await CreateRoomAsync(client, david, "closeds", "Secret");
        using var hidden = await GetAsync(joiner, jason, $"/rooms/{closedId}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        using var people = await GetAsync(client, david, "/autocompletable/users?query=Ja");
        var listed = await people.Content.ReadAsStringAsync();
        Assert.Contains("Jason", listed);
        using var matches = JsonDocument.Parse(listed);
        var jasonId = matches.RootElement[0].GetProperty("id").GetInt64();
        var directId = await CreateRoomAsync(client, david, "directs", null, jasonId);
        using var direct = await GetAsync(client, david, $"/rooms/{directId}");
        Assert.Contains("Jason", await direct.Content.ReadAsStringAsync());
        using var directEdit = await GetAsync(client, david, $"/rooms/directs/{directId}/edit");
        Assert.Contains("Jason", await directEdit.Content.ReadAsStringAsync());

        using var cycle = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{openId}/involvement")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["involvement"] = "Everything" }),
        };
        HttpFlow.Add(cycle, david);
        using var cycled = await client.SendAsync(cycle);
        Assert.Equal(HttpStatusCode.Redirect, cycled.StatusCode);
        using var bell = await GetAsync(client, david, $"/rooms/{openId}");
        Assert.Contains("notification-bell-everything.svg", await bell.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_message_permalink_renders_the_message_and_an_attachment()
    {
        using var factory = new CampfireFactory();
        using var client = HttpFlow.Client(factory);
        var david = await HttpFlow.FirstRunAsync(client);
        var roomId = await HomeRoomAsync(client, david);
        using var post = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{roomId}/messages")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["body"] = "permalink please" }),
        };
        HttpFlow.Add(post, david);
        using var posted = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);

        using var room = await GetAsync(client, david, $"/rooms/{roomId}");
        var html = await room.Content.ReadAsStringAsync();
        var messageId = Regex.Match(html, $"/rooms/{roomId}/@(\\d+)").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(messageId));
        using var permalink = await GetAsync(client, david, $"/rooms/{roomId}/@{messageId}");
        Assert.Contains("permalink please", await permalink.Content.ReadAsStringAsync());

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{roomId}/messages");
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(new StringContent("see this"), "body");
        form.Add(file, "attachment", "dot.png");
        upload.Content = form;
        HttpFlow.Add(upload, david);
        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);

        using var withFile = await GetAsync(client, david, $"/rooms/{roomId}");
        var withFileHtml = await withFile.Content.ReadAsStringAsync();
        var attachment = Regex.Match(withFileHtml, "/messages/(\\d+)/attachment");
        Assert.True(attachment.Success);
        Assert.Contains("lightbox-link", withFileHtml);
        using var image = await client.GetAsync(attachment.Value);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());
        using var download = await client.GetAsync(attachment.Value + "?download=1");
        Assert.Contains("attachment", download.Content.Headers.ContentDisposition?.ToString());
    }

    [Fact]
    public async Task The_bot_api_accepts_a_raw_body_json_and_a_file()
    {
        using var factory = new CampfireFactory();
        using var client = HttpFlow.Client(factory);
        var david = await HttpFlow.FirstRunAsync(client);
        var roomId = await HomeRoomAsync(client, david);
        using var create = new HttpRequestMessage(HttpMethod.Post, "/account/bots")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "Helper", ["webhook_url"] = "" }),
        };
        HttpFlow.Add(create, david);
        using var created = await client.SendAsync(create);
        var key = (await created.Content.ReadAsStringAsync()).Trim();
        Assert.Contains('-', key);

        using var raw = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{roomId}/{key}/messages")
        {
            Content = new ByteArrayContent("Hello from Helper"u8.ToArray()),
        };
        raw.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        using var rawResponse = await client.SendAsync(raw);
        Assert.Equal(HttpStatusCode.Created, rawResponse.StatusCode);
        Assert.Contains("Hello from Helper", await rawResponse.Content.ReadAsStringAsync());

        using var json = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{roomId}/{key}/messages")
        {
            Content = new StringContent("""{"body":"json hello"}""", Encoding.UTF8, "application/json"),
        };
        using var jsonResponse = await client.SendAsync(json);
        var jsonBody = await jsonResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, jsonResponse.StatusCode);
        using var document = JsonDocument.Parse(jsonBody);
        var messageId = document.RootElement.GetProperty("id").GetInt64();
        Assert.Contains("json hello", jsonBody);

        using var index = await client.GetAsync($"/rooms/{roomId}/{key}/messages");
        Assert.Equal("2", index.Headers.GetValues("X-Total-Count").Single());
        Assert.Contains("Hello from Helper", await index.Content.ReadAsStringAsync());

        using var update = new HttpRequestMessage(HttpMethod.Put, $"/rooms/{roomId}/{key}/messages/{messageId}")
        {
            Content = new StringContent("updated", Encoding.UTF8, "text/plain"),
        };
        using var updated = await client.SendAsync(update);
        Assert.Contains("updated", await updated.Content.ReadAsStringAsync());

        using var boost = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{roomId}/{key}/messages/{messageId}/boosts")
        {
            Content = new StringContent("nice", Encoding.UTF8, "text/plain"),
        };
        using var boosted = await client.SendAsync(boost);
        Assert.Equal(HttpStatusCode.Created, boosted.StatusCode);
        Assert.Contains("nice", await boosted.Content.ReadAsStringAsync());
        using var unboost = new HttpRequestMessage(HttpMethod.Delete, $"/rooms/{roomId}/{key}/messages/{messageId}/boosts");
        using var removedBoost = await client.SendAsync(unboost);
        Assert.Equal(HttpStatusCode.NoContent, removedBoost.StatusCode);

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{roomId}/{key}/messages");
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "attachment", "dot.png");
        upload.Content = form;
        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        Assert.Contains("dot.png", await uploaded.Content.ReadAsStringAsync());

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/rooms/{roomId}/{key}/messages/{messageId}");
        using var deleted = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var page = await GetAsync(client, david, "/account/bots");
        var botsHtml = await page.Content.ReadAsStringAsync();
        Assert.Contains("curl -d", botsHtml);
        Assert.Contains("Hello from Helper", botsHtml);
        Assert.Contains(key, botsHtml);
    }

    [Fact]
    public async Task Logo_transfer_unfurl_and_pwa_routes_respond()
    {
        var html = """
            <html><head>
              <meta property="og:title" content="A public page">
              <meta property="og:description" content="Enough to preview">
            </head></html>
            """;
        using var factory = new UnfurlFactory(html);
        using var client = HttpFlow.Client(factory);
        var david = await HttpFlow.FirstRunAsync(client);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        using var upload = new HttpRequestMessage(HttpMethod.Post, "/account/logo");
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "logo", "logo.png");
        upload.Content = form;
        HttpFlow.Add(upload, david);
        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Redirect, uploaded.StatusCode);
        using var logo = await client.GetAsync("/account/logo");
        Assert.Equal(png, await logo.Content.ReadAsByteArrayAsync());
        using var small = await client.GetAsync("/account/logo?size=small");
        Assert.Equal(png, await small.Content.ReadAsByteArrayAsync());

        using var remove = new HttpRequestMessage(HttpMethod.Post, "/account/logo/remove");
        HttpFlow.Add(remove, david);
        using var removed = await client.SendAsync(remove);
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        using var stock = await client.GetAsync("/account/logo");
        Assert.NotEqual(png, await stock.Content.ReadAsByteArrayAsync());

        using var profile = await GetAsync(client, david, "/users/me/profile");
        var profileHtml = await profile.Content.ReadAsStringAsync();
        Assert.Contains("session_transfer_url", profileHtml);
        Assert.Contains("data-enable-push", profileHtml);
        var transfer = Regex.Match(profileHtml, "id=\"session_transfer_url\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.Contains("/session/transfers/", transfer);
        using var guest = HttpFlow.Client(factory);
        using var transferPage = await guest.GetAsync(transfer);
        var transferHtml = await transferPage.Content.ReadAsStringAsync();
        Assert.Contains("id=\"transfer\"", transferHtml);
        var guestCookies = new Dictionary<string, string>();
        HttpFlow.Collect(transferPage, guestCookies);
        using var redeem = new HttpRequestMessage(HttpMethod.Post, new Uri(transfer).PathAndQuery)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["authenticity_token"] = HttpFlow.Token(transferHtml) }),
        };
        HttpFlow.Add(redeem, guestCookies);
        using var redeemed = await guest.SendAsync(redeem);
        Assert.Equal(HttpStatusCode.Redirect, redeemed.StatusCode);
        HttpFlow.Collect(redeemed, guestCookies);
        using var home = await GetAsync(guest, guestCookies, "/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.StartsWith("/rooms/", home.Headers.Location!.OriginalString);

        var roomId = await HomeRoomAsync(client, david);
        using var room = await GetAsync(client, david, $"/rooms/{roomId}");
        var roomHtml = await room.Content.ReadAsStringAsync();
        HttpFlow.Collect(room, david);
        using var unfurl = new HttpRequestMessage(HttpMethod.Post, "/unfurl_link")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["url"] = "http://93.184.216.34/page" }),
        };
        unfurl.Headers.TryAddWithoutValidation("X-CSRF-Token", Csrf(roomHtml));
        unfurl.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        HttpFlow.Add(unfurl, david);
        using var preview = await client.SendAsync(unfurl);
        var previewJson = await preview.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("A public page", previewJson);
        Assert.Contains("og-embed", previewJson);

        using var manifest = await client.GetAsync("/webmanifest");
        Assert.Contains("Campfire", await manifest.Content.ReadAsStringAsync());
        Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType?.MediaType);
        using var worker = await client.GetAsync("/service-worker");
        Assert.Contains("showNotification", await worker.Content.ReadAsStringAsync());
        using var sounds = await client.GetAsync("/sounds");
        Assert.Contains("bell", await sounds.Content.ReadAsStringAsync());
        using var qr = await client.GetAsync("/qr_code/" + QrCodeToken("https://example.com/join"));
        Assert.Contains("<svg", await qr.Content.ReadAsStringAsync());
        using var bad = await client.GetAsync("/qr_code/" + QrCodeToken("not a url"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var key = await client.GetAsync("/web_push/public_key");
        Assert.False(string.IsNullOrWhiteSpace(await key.Content.ReadAsStringAsync()));
    }

    private static async Task<long> HomeRoomAsync(HttpClient client, Dictionary<string, string> cookies)
    {
        using var home = await GetAsync(client, cookies, "/");
        return long.Parse(home.Headers.Location!.OriginalString.Split('/').Last());
    }

    private static async Task<string> JoinCodeAsync(HttpClient client, Dictionary<string, string> cookies)
    {
        using var account = await GetAsync(client, cookies, "/account");
        var html = await account.Content.ReadAsStringAsync();
        return Regex.Match(html, "id=\"join-code\">([^<]+)<").Groups[1].Value;
    }

    private static async Task<Dictionary<string, string>> JoinAsync(HttpClient client, string code, string name, string email)
    {
        var cookies = new Dictionary<string, string>();
        using var page = await client.GetAsync($"/join/{code}");
        HttpFlow.Collect(page, cookies);
        using var join = new HttpRequestMessage(HttpMethod.Post, $"/join/{code}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["name"] = name,
                ["email_address"] = email,
                ["password"] = "secret123456",
                ["authenticity_token"] = HttpFlow.Token(await page.Content.ReadAsStringAsync()),
            }),
        };
        HttpFlow.Add(join, cookies);
        using var joined = await client.SendAsync(join);
        HttpFlow.Collect(joined, cookies);
        return cookies;
    }

    private static async Task<long> CreateRoomAsync(HttpClient client, Dictionary<string, string> cookies, string kind, string? name, long? userId = null)
    {
        using var page = await GetAsync(client, cookies, $"/rooms/{kind}/new");
        var html = await page.Content.ReadAsStringAsync();
        var fields = new Dictionary<string, string> { ["authenticity_token"] = HttpFlow.Token(html) };
        if (name is not null)
            fields["room[name]"] = name;
        if (userId is not null)
            fields["user_ids"] = userId.Value.ToString();
        using var post = new HttpRequestMessage(HttpMethod.Post, $"/rooms/{kind}/new")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        HttpFlow.Add(post, cookies);
        using var created = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        return long.Parse(created.Headers.Location!.OriginalString.Split('/').Last());
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Dictionary<string, string> cookies, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        HttpFlow.Add(request, cookies);
        var response = await client.SendAsync(request);
        HttpFlow.Collect(response, cookies);
        return response;
    }

    private static string Csrf(string html)
    {
        var match = Regex.Match(html, "name=\"csrf-token\" content=\"([^\"]*)\"");
        if (!match.Success)
            throw new InvalidOperationException("page has no csrf meta tag");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string QrCodeToken(string text) => Campfire.Core.QrCode.Token(text);

    private sealed class UnfurlFactory(string html) : CampfireFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<ILinkFetcher>(new FixedFetcher(html)));
        }
    }

    private sealed class FixedFetcher(string html) : ILinkFetcher
    {
        public Task<string?> GetHtmlAsync(string url, CancellationToken cancellationToken = default) => Task.FromResult<string?>(html);
    }
}
