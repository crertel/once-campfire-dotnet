using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campfire.Core;
using Campfire.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Campfire.Tests;

public sealed class UiBrowserTests
{
    [Fact]
    public async Task Setup_and_room_pages_use_the_rails_type_and_grid()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"campfire-ui-{Guid.NewGuid():n}");
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
        var capture = Environment.GetEnvironmentVariable("CAMPFIRE_UI_CAPTURE");
        var console = new StringBuilder();
        try
        {
            await using var browser = await ChromeSession.Start("America/Los_Angeles");
            await using var page = await browser.Open();
            await page.LightScheme();
            await page.Size(1280, 800, false);
            await page.Go($"http://127.0.0.1:{port}/");
            await page.WaitFor("!!(document.body && document.body.innerText.includes('Set up Campfire') && document.querySelector('link[rel=\"stylesheet\"]').sheet)");
            var setupFont = await page.Eval("getComputedStyle(document.body).fontFamily");
            var setupBackground = await page.Eval(BackgroundScript);
            AssertSystemFont(setupFont);
            Assert.Equal("match", setupBackground);
            await page.Eval("document.querySelector('input[name=\"user[name]\"]').focus()");
            await page.Call("Input.insertText", new Dictionary<string, object> { ["text"] = "Ada" });
            Assert.Equal("Ada", await page.Eval("document.querySelector('input[name=\"user[name]\"]').value"));
            await page.Shot(capture, "setup-desktop.png");
            await page.Size(390, 844, true);
            await page.Shot(capture, "setup-narrow.png");
            Assert.Equal("match", await page.Eval(BackgroundScript));

            var cookies = await HttpFlow.FirstRunAsync(client);
            using var home = new HttpRequestMessage(HttpMethod.Get, "/");
            HttpFlow.Add(home, cookies);
            using var redirected = await client.SendAsync(home);
            var roomPath = redirected.Headers.Location!.OriginalString;
            using var post = new HttpRequestMessage(HttpMethod.Post, roomPath + "/messages")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["body"] = "Hello from the room" }),
            };
            HttpFlow.Add(post, cookies);
            using var posted = await client.SendAsync(post);
            Assert.Equal(HttpStatusCode.Redirect, posted.StatusCode);
            await StampHelloAsync(Path.Combine(directory, "db", "campfire.sqlite"));

            await page.Size(1280, 800, false);
            await page.Go($"http://127.0.0.1:{port}{roomPath}", cookies["session_token"]);
            await page.WaitFor(OwnMessageScript);
            var roomFont = await page.Eval("getComputedStyle(document.body).fontFamily");
            var display = await page.Eval("getComputedStyle(document.body).display");
            AssertSystemFont(roomFont);
            Assert.Equal("grid", display);
            Assert.Equal("match", await page.Eval(BackgroundScript));
            Assert.True(int.Parse(await page.Eval("String(document.body.scrollHeight)")) > 100);
            await page.Shot(capture, "room-desktop.png");
            await page.Size(390, 844, true);
            var sidebar = "animating";
            var settled = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < settled && sidebar != "translate(100%)")
            {
                sidebar = await page.Eval(SidebarTransform);
                if (sidebar != "translate(100%)")
                    await Task.Delay(50);
            }

            Assert.Equal("translate(100%)", sidebar);
            Assert.Equal("grid", await page.Eval("getComputedStyle(document.body).display"));
            await page.Shot(capture, "room-narrow.png");
            console.Append(page.ConsoleLog());
            Assert.True(page.Errors.Count == 0, string.Join('\n', page.Errors));
        }
        finally
        {
            if (!string.IsNullOrEmpty(capture))
                await File.WriteAllTextAsync(Path.Combine(capture, "page-console.txt"), console.Length == 0 ? "no page errors\n" : console.ToString());
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task New_room_composer_and_profile_controls_work_in_the_browser()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"campfire-ui-product-{Guid.NewGuid():n}");
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
        var origin = $"http://127.0.0.1:{port}";
        try
        {
            var cookies = await HttpFlow.FirstRunAsync(client);
            using var home = new HttpRequestMessage(HttpMethod.Get, "/");
            HttpFlow.Add(home, cookies);
            using var redirected = await client.SendAsync(home);
            var roomPath = redirected.Headers.Location!.OriginalString;
            using var account = new HttpRequestMessage(HttpMethod.Get, "/account");
            HttpFlow.Add(account, cookies);
            using var accountResponse = await client.SendAsync(account);
            var code = Regex.Match(await accountResponse.Content.ReadAsStringAsync(), "id=\"join-code\">([^<]+)<").Groups[1].Value;
            using var joiner = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = client.BaseAddress };
            var jason = new Dictionary<string, string>();
            using var joinPage = await joiner.GetAsync($"/join/{code}");
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
            using var joined = await joiner.SendAsync(join);
            Assert.Equal(HttpStatusCode.Redirect, joined.StatusCode);

            await using var browser = await ChromeSession.Start();
            await using var page = await browser.Open();
            await page.LightScheme();
            await page.Size(1280, 800, false);
            await page.Go(origin + roomPath, cookies["session_token"]);
            await page.WaitFor("document.documentElement.dataset.campfireConnected === '1' && !!document.querySelector('#composer')");
            await page.Eval("document.querySelector('[data-emoji]').click()");
            await page.WaitFor("!![...document.querySelectorAll('[data-emoji-panel] button')].some((button) => button.textContent === '/bell')");
            await page.Eval("document.querySelector('[data-rich-text]').click()");
            await page.WaitFor("document.querySelector('[data-editor]') && document.querySelector('[data-toolbar]').innerText.includes('bold')");
            await page.Eval("document.querySelector('[data-editor]').innerHTML = '<strong>bold hello</strong>'");
            await page.Eval("document.querySelector('#composer-frame').requestSubmit()");
            await page.WaitFor("document.body.innerText.includes('bold hello') && document.documentElement.dataset.campfireConnected === '1'");
            Assert.Contains("<strong>bold hello</strong>", await page.Eval("document.body.innerHTML"));

            await page.Eval("document.querySelector('a[href=\"/rooms/opens/new\"]').click()");
            await page.WaitFor("!!document.querySelector('input[name=\"room[name]\"]')");
            await page.Eval("document.querySelector('a[href=\"/rooms/closeds/new\"]').click()");
            await page.WaitFor("!!document.querySelector('[data-filter]')");
            await page.Eval("""
                (() => {
                  const input = document.querySelector('[data-filter]');
                  input.value = 'jason';
                  input.dispatchEvent(new Event('input', { bubbles: true }));
                })()
                """);
            await page.WaitFor("""
                (() => {
                  const jason = document.querySelector('[data-name="Jason"]');
                  const david = document.querySelector('[data-name="David"]');
                  return !!(jason && !jason.hidden && david && david.hidden);
                })()
                """);
            await page.Size(390, 844, true);
            var width = int.Parse(await page.Eval("String(Math.round(document.querySelector('input[name=\"room[name]\"]').getBoundingClientRect().width))"));
            Assert.InRange(width, 200, 390);

            await page.Size(1280, 800, false);
            await page.Go(origin + "/users/me/profile", cookies["session_token"]);
            await page.WaitFor("!!document.querySelector('#session_transfer_url')");
            Assert.Contains("/session/transfers/", await page.Eval("document.querySelector('#session_transfer_url').value"));
            Assert.StartsWith("/qr_code/", await page.Eval("document.querySelector('a[href^=\"/qr_code/\"]').getAttribute('href')"));
            await page.Go(origin + "/account/bots", cookies["session_token"]);
            await page.WaitFor("document.body.innerText.includes('Chat bots')");
            await page.Go(origin + roomPath, cookies["session_token"]);
            await page.WaitFor("document.documentElement.dataset.campfireConnected === '1' && !!document.querySelector('a[href*=\"/edit\"]')");
            await page.Eval("document.querySelector('a[href*=\"/edit\"]').click()");
            await page.WaitFor("!!document.querySelector('input[name=\"room[name]\"]')");
            Assert.True(page.Errors.Count == 0, string.Join('\n', page.Errors));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private const string OwnMessageScript = """
        (() => {
          const sheet = document.querySelector('link[rel="stylesheet"]').sheet;
          const node = [...document.querySelectorAll('.message')].find((item) => (item.innerText || '').includes('Hello from the room'));
          if (!sheet || !node || !node.classList.contains('message--me'))
            return false;
          const areas = getComputedStyle(node).gridTemplateAreas.replace(/"/g, '');
          if (!areas.trim().endsWith('avatar'))
            return false;
          const date = node.querySelector('[data-local-time-target="date"]');
          const time = node.querySelector('[data-local-time-target="time"]');
          if (!date || date.textContent !== 'Oct 4' || !time || time.textContent !== '10:04 PM')
            return false;
          const text = document.body.innerText;
          return !text.includes('5:04 AM') && !text.includes('October 5, 2026');
        })()
        """;

    private static async Task StampHelloAsync(string database)
    {
        var options = new DbContextOptionsBuilder<CampfireDb>().UseSqlite(Seeder.Connection(database)).Options;
        await using var db = new CampfireDb(options);
        var message = await db.Messages.SingleAsync(item => item.PlainText == "Hello from the room");
        message.CreatedAt = new DateTime(2026, 10, 5, 5, 4, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
    }

    private const string SidebarTransform = """
        (() => {
          const el = document.getElementById('sidebar');
          if (!el)
            return 'missing';
          if (el.classList.contains('open'))
            return 'open';
          if (el.getAnimations().some(animation => animation.playState === 'running'))
            return 'animating';
          const style = getComputedStyle(el);
          const match = style.transform.match(/matrix\(([^)]+)\)/);
          if (!match)
            return style.position + ' ' + (style.transform || 'none');
          const tx = parseFloat(match[1].split(',')[4]);
          const width = el.getBoundingClientRect().width;
          if (style.position === 'fixed' && Number.isFinite(tx) && Math.abs(tx - width) <= 1 && width >= window.innerWidth - 1)
            return 'translate(100%)';
          return style.position + ' ' + style.transform + ' w=' + Math.round(width);
        })()
        """;

    private static void AssertSystemFont(string font)
    {
        Assert.Contains("-apple-system", font);
        Assert.Contains("Segoe UI", font);
        Assert.DoesNotContain("Georgia", font);
        Assert.DoesNotContain("Times New Roman", font);
    }

    private const string BackgroundScript = """
        (() => {
          const probe = document.createElement('div');
          probe.style.backgroundColor = 'var(--color-bg)';
          document.body.append(probe);
          const expected = getComputedStyle(probe).backgroundColor;
          probe.remove();
          return getComputedStyle(document.body).backgroundColor === expected ? 'match' : getComputedStyle(document.body).backgroundColor + ' vs ' + expected;
        })()
        """;

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

    private sealed class ChromeSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _profile;
        private readonly int _port;

        private ChromeSession(Process process, string profile, int port)
        {
            _process = process;
            _profile = profile;
            _port = port;
        }

        public static async Task<ChromeSession> Start(string? timeZone = null)
        {
            var profile = Path.Combine(Path.GetTempPath(), $"campfire-ui-chrome-{Guid.NewGuid():n}");
            Directory.CreateDirectory(profile);
            var chrome = File.Exists("/run/current-system/sw/bin/chromium")
                ? "/run/current-system/sw/bin/chromium"
                : "chromium";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = chrome,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };
            if (!string.IsNullOrEmpty(timeZone))
                process.StartInfo.Environment["TZ"] = timeZone;
            foreach (var argument in new[]
            {
                "--headless=new", "--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage",
                "--remote-debugging-port=0", "--remote-allow-origins=*", $"--user-data-dir={profile}",
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
            return new ChromeSession(process, profile, await ready.Task);
        }

        public async Task<ChromePage> Open()
        {
            using var http = new HttpClient();
            using var created = await http.PutAsync($"http://127.0.0.1:{_port}/json/new?about:blank", new StringContent(""));
            var json = created.IsSuccessStatusCode
                ? await created.Content.ReadAsStringAsync()
                : await http.GetStringAsync($"http://127.0.0.1:{_port}/json/new?about:blank");
            using var document = JsonDocument.Parse(json);
            return await ChromePage.ConnectAsync(new Uri(document.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!));
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

    private sealed class ChromePage : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly ConcurrentQueue<string> _console = new();
        private int _next;

        public List<string> Errors { get; } = [];

        public static async Task<ChromePage> ConnectAsync(Uri uri)
        {
            var page = new ChromePage();
            await page._socket.ConnectAsync(uri, CancellationToken.None);
            _ = page.ReadLoop();
            await page.Call("Console.enable");
            await page.Call("Page.enable");
            return page;
        }

        public Task LightScheme() => Call("Emulation.setEmulatedMedia", new Dictionary<string, object>
        {
            ["features"] = new object[] { new Dictionary<string, object> { ["name"] = "prefers-color-scheme", ["value"] = "light" } },
        });

        public Task Size(int width, int height, bool mobile) => Call("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
        {
            ["width"] = width,
            ["height"] = height,
            ["deviceScaleFactor"] = 1,
            ["mobile"] = mobile,
        });

        public async Task Go(string url, string? sessionToken = null)
        {
            if (!string.IsNullOrEmpty(sessionToken))
            {
                await Call("Network.setCookie", new Dictionary<string, object>
                {
                    ["name"] = "session_token",
                    ["value"] = sessionToken,
                    ["url"] = url,
                    ["path"] = "/",
                    ["httpOnly"] = true,
                    ["secure"] = false,
                    ["sameSite"] = "Lax",
                });
            }

            await Call("Page.navigate", new Dictionary<string, object> { ["url"] = url });
        }

        public async Task Shot(string? directory, string name)
        {
            if (string.IsNullOrEmpty(directory))
                return;
            Directory.CreateDirectory(directory);
            var shot = await Call("Page.captureScreenshot", new Dictionary<string, object> { ["format"] = "png" });
            var data = shot.GetProperty("data").GetString() ?? "";
            await File.WriteAllBytesAsync(Path.Combine(directory, name), Convert.FromBase64String(data));
        }

        public string ConsoleLog() => _console.IsEmpty ? "no page errors\n" : string.Join('\n', _console) + "\n";

        public async Task<JsonElement> Call(string method, object? parameters = null)
        {
            var id = Interlocked.Increment(ref _next);
            var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiter;
            var payloadBody = new Dictionary<string, object?> { ["id"] = id, ["method"] = method };
            if (parameters is not null)
                payloadBody["params"] = parameters;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payloadBody));
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
                    last = await Eval("document.body ? document.body.innerText.slice(0, 300) : ''");
                }
                catch (InvalidOperationException exception)
                {
                    last = exception.Message;
                }

                await Task.Delay(200);
            }

            throw new TimeoutException(expression + " :: " + last);
        }

        public Task<string> Eval(string expression) => Eval(expression, true);

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            _socket.Dispose();
        }

        private async Task<string> Eval(string expression, bool _)
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
            var buffer = new byte[256 * 1024];
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
                    var root = document.RootElement;
                    if (root.TryGetProperty("method", out var method))
                    {
                        var name = method.GetString();
                        if (name is "Console.messageAdded" && root.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("message", out var entry))
                        {
                            var level = entry.TryGetProperty("level", out var levelValue) ? levelValue.GetString() : "";
                            var line = entry.TryGetProperty("text", out var textValue) ? textValue.GetString() ?? "" : "";
                            _console.Enqueue(level + ": " + line);
                            if (level is "error" && !line.Contains("favicon", StringComparison.OrdinalIgnoreCase))
                                Errors.Add(line);
                        }

                        continue;
                    }

                    if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number)
                        continue;
                    if (!_pending.TryRemove(id.GetInt32(), out var waiter))
                        continue;
                    if (root.TryGetProperty("error", out var error))
                        waiter.TrySetException(new InvalidOperationException(error.ToString()));
                    else
                        waiter.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
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
