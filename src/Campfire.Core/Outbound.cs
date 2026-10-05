namespace Campfire.Core;

public sealed record WebhookCall(long BotId, long RoomId, string Url, string Json);

public sealed record PushCall(string Endpoint, string P256dh, string Auth, string Title, string Body, string Path, int Badge);

public interface IOutbound
{
    void EnqueueWebhook(WebhookCall call);

    void EnqueuePush(PushCall call);
}

public sealed class RecordingOutbound : IOutbound
{
    public List<WebhookCall> Webhooks { get; } = [];

    public List<PushCall> Pushes { get; } = [];

    public void EnqueueWebhook(WebhookCall call) => Webhooks.Add(call);

    public void EnqueuePush(PushCall call) => Pushes.Add(call);
}
