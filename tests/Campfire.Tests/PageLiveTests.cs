using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Campfire.Tests;

public sealed class PageLiveTests
{
    [Fact]
    public async Task Rendered_room_and_sidebar_pages_show_a_message_unread_and_presence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"campfire-live-{Guid.NewGuid():n}");
        Directory.CreateDirectory(directory);
        var port = FreePort();
        var content = WebContentRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = "Development",
            ContentRootPath = content,
            WebRootPath = Path.Combine(content, "wwwroot"),
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Campfire:Database"] = Path.Combine(directory, "db", "campfire.sqlite"),
        });
        var app = CampfireWeb.Build(builder);
        await app.StartAsync();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        try
        {
            await using var jasonBrowser = await Chrome.Start();
            await using var davidBrowser = await Chrome.Start();
            var david = await HttpFlow.FirstRunAsync(client);
            using var account = new HttpRequestMessage(HttpMethod.Get, "/account");
            HttpFlow.Add(account, david);
            using var accountResponse = await client.SendAsync(account);
            var code = Regex.Match(await accountResponse.Content.ReadAsStringAsync(), "id=\"join-code\">([^<]+)<").Groups[1].Value;
            Assert.False(string.IsNullOrWhiteSpace(code));

            var jason = new Dictionary<string, string>();
            using var joinPage = await client.GetAsync($"/join/{code}");
            HttpFlow.Collect(joinPage, jason);
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
            HttpFlow.Add(join, jason);
            using var joined = await client.SendAsync(join);
            HttpFlow.Collect(joined, jason);
            Assert.False(string.IsNullOrEmpty(jason["session_token"]));

            using var home = new HttpRequestMessage(HttpMethod.Get, "/");
            HttpFlow.Add(home, david);
            using var redirected = await client.SendAsync(home);
            var roomPath = redirected.Headers.Location!.OriginalString;
            using var room = new HttpRequestMessage(HttpMethod.Get, roomPath);
            HttpFlow.Add(room, jason);
            using var roomResponse = await client.SendAsync(room);
            var roomHtml = await roomResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, roomResponse.StatusCode);
            var clientScript = await ServedClientAsync(client, roomHtml);
            Assert.Contains("/hubs/campfire", clientScript);
            Assert.Contains("JoinRoom", clientScript);

            using var sidebar = new HttpRequestMessage(HttpMethod.Get, "/users/me/sidebar");
            HttpFlow.Add(sidebar, jason);
            using var sidebarResponse = await client.SendAsync(sidebar);
            var sidebarHtml = await sidebarResponse.Content.ReadAsStringAsync();
            Assert.Contains("data-live", sidebarHtml);
            Assert.Contains("data-sidebar-room", sidebarHtml);
            Assert.Contains(clientScript, await ServedClientAsync(client, sidebarHtml));

            using var seeded = new HttpRequestMessage(HttpMethod.Post, roomPath + "/messages")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["body"] = "already on the page" }),
            };
            HttpFlow.Add(seeded, david);
            using var seededResponse = await client.SendAsync(seeded);
            Assert.Equal(HttpStatusCode.Redirect, seededResponse.StatusCode);

            var origin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
            await using var jasonRoom = await jasonBrowser.Open(origin, jason["session_token"], origin + roomPath);
            await jasonRoom.WaitFor("document.documentElement.dataset.campfireConnected === '1'");
            await using var davidRoom = await davidBrowser.Open(origin, david["session_token"], origin + roomPath);
            await davidRoom.WaitFor("document.documentElement.dataset.campfireConnected === '1'");
            await jasonRoom.WaitFor("document.querySelector('#presence .present') !== null");

            await using var jasonSidebar = await jasonBrowser.Open(origin, jason["session_token"], origin + "/users/me/sidebar");
            await jasonSidebar.WaitFor("document.documentElement.dataset.campfireConnected === '1'");

            using var post = new HttpRequestMessage(HttpMethod.Post, roomPath + "/messages")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["body"] = "live from the page" }),
            };
            HttpFlow.Add(post, david);
            using var posted = await client.SendAsync(post);
            Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);

            await jasonRoom.WaitFor("document.body.innerText.includes('live from the page')");
            await jasonRoom.WaitFor("""
                (() => {
                  function shape(text) {
                    const node = [...document.querySelectorAll('.message')].find((item) => (item.innerText || '').includes(text));
                    if (!node) return '';
                    const names = ['message__day-separator','message__avatar','message__author','message__timestamp','message__body','message__body-content','boosts'];
                    return names.every((name) => node.querySelector('.' + name)) ? names.join(' ') : 'missing';
                  }
                  const server = shape('already on the page');
                  const live = shape('live from the page');
                  return server !== '' && server !== 'missing' && server === live;
                })()
                """);
            await jasonSidebar.WaitFor("document.querySelector('[data-sidebar-room].unread') !== null");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static async Task<string> ServedClientAsync(HttpClient client, string html)
    {
        string? clientScript = null;
        foreach (Match match in Regex.Matches(html, "<script\\b[^>]*\\bsrc=\"([^\"]+)\""))
        {
            var src = WebUtility.HtmlDecode(match.Groups[1].Value);
            using var response = await client.GetAsync(src);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (body.Contains("JoinRoom", StringComparison.Ordinal) && body.Contains("/hubs/campfire", StringComparison.Ordinal))
                clientScript = body;
        }

        return clientScript ?? throw new InvalidOperationException("rendered page has no live client");
    }

    private static string WebContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Campfire.Web");
            if (File.Exists(Path.Combine(candidate, "Campfire.Web.csproj")))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find src/Campfire.Web.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Chrome : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _profile;
        private readonly int _port;

        private Chrome(Process process, string profile, int port)
        {
            _process = process;
            _profile = profile;
            _port = port;
        }

        public static async Task<Chrome> Start()
        {
            var profile = Path.Combine(Path.GetTempPath(), $"campfire-chrome-{Guid.NewGuid():n}");
            Directory.CreateDirectory(profile);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "chromium",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };
            foreach (var argument in new[]
            {
                "--headless=new",
                "--disable-gpu",
                "--no-sandbox",
                "--disable-dev-shm-usage",
                "--remote-debugging-port=0",
                "--remote-allow-origins=*",
                $"--user-data-dir={profile}",
            })
                process.StartInfo.ArgumentList.Add(argument);

            var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var log = new ConcurrentQueue<string>();
            void Watch(string? line)
            {
                if (line is null)
                    return;
                log.Enqueue(line);
                var match = Regex.Match(line, @"127\.0\.0\.1:(\d+)/");
                if (match.Success)
                    ready.TrySetResult(int.Parse(match.Groups[1].Value));
            }

            process.OutputDataReceived += (_, eventArgs) => Watch(eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => Watch(eventArgs.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var completed = await Task.WhenAny(ready.Task, process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(20)));
            if (completed != ready.Task)
                throw new InvalidOperationException("chromium did not open a debugging port: " + string.Join('\n', log));
            return new Chrome(process, profile, await ready.Task);
        }

        public async Task<DevtoolsPage> Open(string origin, string sessionToken, string url)
        {
            using var http = new HttpClient();
            using var created = await http.PutAsync($"http://127.0.0.1:{_port}/json/new?about:blank", new StringContent(""));
            if (!created.IsSuccessStatusCode)
            {
                using var fallback = await http.GetAsync($"http://127.0.0.1:{_port}/json/new?about:blank");
                fallback.EnsureSuccessStatusCode();
                return await Connect(await fallback.Content.ReadAsStringAsync(), origin, sessionToken, url);
            }

            return await Connect(await created.Content.ReadAsStringAsync(), origin, sessionToken, url);
        }

        private static async Task<DevtoolsPage> Connect(string json, string origin, string sessionToken, string url)
        {
            using var document = JsonDocument.Parse(json);
            var page = await DevtoolsPage.ConnectAsync(new Uri(document.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!));
            await page.Call("Network.setCookie", new Dictionary<string, object>
            {
                ["name"] = "session_token",
                ["value"] = sessionToken,
                ["url"] = origin,
                ["path"] = "/",
                ["httpOnly"] = true,
                ["secure"] = false,
                ["sameSite"] = "Lax",
            });
            await page.Call("Page.navigate", new Dictionary<string, object> { ["url"] = url });
            return page;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.Dispose();
            if (Directory.Exists(_profile))
                Directory.Delete(_profile, true);
            await Task.CompletedTask;
        }
    }

    private sealed class DevtoolsPage : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private int _next;

        public static async Task<DevtoolsPage> ConnectAsync(Uri uri)
        {
            var page = new DevtoolsPage();
            await page._socket.ConnectAsync(uri, CancellationToken.None);
            _ = page.ReadLoop();
            return page;
        }

        public async Task<JsonElement> Call(string method, object? parameters = null)
        {
            var id = Interlocked.Increment(ref _next);
            var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiter;
            var payloadBody = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method,
            };
            if (parameters is not null)
                payloadBody["params"] = parameters;
            var payload = JsonSerializer.Serialize(payloadBody);
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public async Task WaitFor(string expression)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            var last = "";
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (await Eval(expression) == "true")
                        return;
                    last = await Eval("document.documentElement.dataset.campfireError || (document.body && document.body.innerText.slice(0, 500)) || ''");
                }
                catch (InvalidOperationException exception)
                {
                    last = exception.Message;
                }

                await Task.Delay(200);
            }

            throw new TimeoutException(expression + " :: " + last);
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            _socket.Dispose();
        }

        private async Task<string> Eval(string expression)
        {
            var result = await Call("Runtime.evaluate", new Dictionary<string, object>
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
            });
            if (!result.TryGetProperty("result", out var remote))
                return "";
            if (remote.TryGetProperty("value", out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.String => value.GetString() ?? "",
                    _ => value.ToString(),
                };
            }

            return remote.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "";
        }

        private async Task ReadLoop()
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    var received = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (received.MessageType == WebSocketMessageType.Close)
                        break;
                    message.Write(buffer, 0, received.Count);
                    if (!received.EndOfMessage)
                        continue;
                    var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                    message.SetLength(0);
                    using var document = JsonDocument.Parse(text);
                    if (!document.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number)
                        continue;
                    if (!_pending.TryRemove(id.GetInt32(), out var waiter))
                        continue;
                    if (document.RootElement.TryGetProperty("error", out var error))
                        waiter.TrySetException(new InvalidOperationException(error.ToString()));
                    else
                        waiter.TrySetResult(document.RootElement.TryGetProperty("result", out var result) ? result.Clone() : default);
                }
            }
            catch (Exception exception)
            {
                foreach (var waiter in _pending.Values)
                    waiter.TrySetException(exception);
            }
        }
    }
}
