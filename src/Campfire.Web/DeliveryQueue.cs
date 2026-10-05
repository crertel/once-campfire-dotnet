using System.Net.Http.Headers;
using System.Text;
using Campfire.Core;

namespace Campfire.Web;

public sealed class DeliveryQueue(IServiceScopeFactory scopes, IHttpClientFactory http, AppSecrets secrets) : BackgroundService, IOutbound
{
    private readonly ChannelWork _work = new();

    public void EnqueueWebhook(WebhookCall call) => _work.Add(call);

    public void EnqueuePush(PushCall call) => _work.Add(call);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _work.ReadAsync(stoppingToken))
        {
            try
            {
                if (item.Webhook is not null)
                    await DeliverWebhookAsync(item.Webhook, stoppingToken);
                if (item.Push is not null)
                    await DeliverPushAsync(item.Push, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // One failed bot or browser push must not stop the next delivery.
            }
        }
    }

    private async Task DeliverWebhookAsync(WebhookCall call, CancellationToken cancellationToken)
    {
        var client = http.CreateClient("webhooks");
        client.Timeout = TimeSpan.FromSeconds(7);
        using var request = new HttpRequestMessage(HttpMethod.Post, call.Url)
        {
            Content = new StringContent(call.Json, Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            return;
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0 || bytes.Length > FileCabinet.MaxBytes)
            return;
        await using var scope = scopes.CreateAsyncScope();
        var app = scope.ServiceProvider.GetRequiredService<CampfireApp>();
        if (type is "text/plain" or "text/html")
        {
            await app.CreateMessageAsync(call.BotId, call.RoomId, Encoding.UTF8.GetString(bytes), null, null, false, cancellationToken);
            return;
        }

        if (type.StartsWith("image/", StringComparison.Ordinal) || type.StartsWith("audio/", StringComparison.Ordinal) || type.StartsWith("video/", StringComparison.Ordinal))
        {
            var extension = type.Split('/').Last().Split('+')[0];
            await app.CreateMessageAsync(call.BotId, call.RoomId, "", null, new IncomingFile("attachment." + extension, type, bytes), false, cancellationToken);
        }
    }

    private async Task DeliverPushAsync(PushCall call, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secrets.VapidPublicKey) || string.IsNullOrWhiteSpace(secrets.VapidPrivateKey))
            return;
        if (!Uri.TryCreate(call.Endpoint, UriKind.Absolute, out var endpoint))
            return;
        var token = WebPush.Sign(endpoint.GetLeftPart(UriPartial.Authority), "mailto:support@37signals.com", DateTimeOffset.UtcNow.AddHours(12), secrets.VapidPublicKey, secrets.VapidPrivateKey);
        var body = WebPush.Encrypt(Encoding.UTF8.GetBytes(WebPush.Payload(call.Title, call.Body, call.Path, call.Badge)), call.P256dh, call.Auth, out _, out _);
        var client = http.CreateClient("webhooks");
        using var request = new HttpRequestMessage(HttpMethod.Post, call.Endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("Authorization", "vapid t=" + token + ",k=" + secrets.VapidPublicKey);
        request.Headers.TryAddWithoutValidation("TTL", "86400");
        request.Headers.TryAddWithoutValidation("Urgency", "high");
        using var response = await client.SendAsync(request, cancellationToken);
        _ = response.StatusCode;
    }

    private sealed class ChannelWork
    {
        private readonly System.Threading.Channels.Channel<Item> _channel = System.Threading.Channels.Channel.CreateUnbounded<Item>();

        public void Add(WebhookCall call) => _channel.Writer.TryWrite(new Item(call, null));

        public void Add(PushCall call) => _channel.Writer.TryWrite(new Item(null, call));

        public IAsyncEnumerable<Item> ReadAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public readonly record struct Item(WebhookCall? Webhook, PushCall? Push);
    }
}

public interface ILinkFetcher
{
    Task<string?> GetHtmlAsync(string url, CancellationToken cancellationToken = default);
}

public sealed class GuardedLinkFetcher(HttpClient http) : ILinkFetcher
{
    public async Task<string?> GetHtmlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Unfurl.ShouldFetch(url))
            return null;
        var current = url;
        for (var hop = 0; hop < 10; hop++)
        {
            Unfurl.EnsurePublic(current, CampfireApp.SystemDns);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null)
                    return null;
                current = location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(new Uri(current), location).AbsoluteUri;
                continue;
            }

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                return null;
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                return null;
            if (response.Content.Headers.ContentLength is > MaxBytes)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length > MaxBytes ? null : Encoding.UTF8.GetString(bytes);
        }

        return null;
    }

    private const int MaxBytes = 5 * 1024 * 1024;
}
