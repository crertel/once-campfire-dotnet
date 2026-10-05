using System.Net;
using System.Text;
using System.Text.Json;
using Campfire.Core;
using Microsoft.AspNetCore.Antiforgery;

namespace Campfire.Web;

public static partial class CampfireWeb
{
    private static void MapProduct(WebApplication app)
    {
        app.MapPost("/rooms/{id:long}/involvement", InvolvementAsync).DisableAntiforgery();
        app.MapDelete("/rooms/{id:long}", DestroyRoomAsync).DisableAntiforgery();
        app.MapPost("/rooms/{id:long}/delete", DestroyRoomAsync).DisableAntiforgery();
        app.MapGet("/messages/{id:long}/attachment", AttachmentAsync).DisableAntiforgery();
        app.MapGet("/account/logo", LogoAsync).DisableAntiforgery();
        app.MapPost("/account/logo", UploadLogoAsync).DisableAntiforgery();
        app.MapDelete("/account/logo", ClearLogoAsync).DisableAntiforgery();
        app.MapPost("/account/logo/remove", ClearLogoAsync).DisableAntiforgery();
        app.MapGet("/qr_code/{token}", QrAsync).DisableAntiforgery();
        app.MapGet("/autocompletable/users", AutocompleteAsync).DisableAntiforgery();
        app.MapPost("/unfurl_link", UnfurlAsync).DisableAntiforgery();
        app.MapGet("/sounds", () => Results.Json(Sound.All.Select(sound => new { name = sound.Name, text = sound.Text })));
        app.MapGet("/webmanifest", ManifestAsync).DisableAntiforgery();
        app.MapGet("/service-worker", () => Results.Text(ServiceWorker, "application/javascript; charset=utf-8")).DisableAntiforgery();
        app.MapGet("/web_push/public_key", (AppSecrets secrets) => Results.Text(secrets.VapidPublicKey ?? "")).DisableAntiforgery();
    }

    private static async Task<IResult> InvolvementAsync(long id, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Redirect("/session/new");
        var form = await http.Request.ReadFormAsync();
        if (!Enum.TryParse<Involvement>(form["involvement"].ToString(), true, out var involvement))
            return Results.BadRequest();
        await campfire.SetInvolvementAsync(user.Id, id, involvement);
        var back = form["return"].ToString();
        return Results.Redirect(string.IsNullOrEmpty(back) ? $"/rooms/{id}" : back);
    }

    private static async Task<IResult> DestroyRoomAsync(long id, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        await campfire.DeleteRoomAsync(user.Id, id);
        return Results.Redirect("/");
    }

    private static async Task<IResult> AttachmentAsync(long id, string? download, HttpContext http, CampfireApp campfire)
    {
        var attachment = await campfire.AttachmentAsync(id);
        var bytes = await campfire.ReadAttachmentAsync(id);
        if (attachment is null || bytes is null)
            return Results.NotFound();
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var save = download is "1" or "true" or "yes";
        var inline = !save && (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || attachment.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || attachment.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
        if (!inline)
            http.Response.Headers.ContentDisposition = "attachment; filename=\"" + attachment.FileName + "\"";
        return Results.Bytes(bytes, attachment.ContentType);
    }

    private static async Task<IResult> LogoAsync(string? size, CampfireApp campfire)
    {
        if (!await campfire.HasAccountAsync())
            return StockLogo(size == "small");
        var account = await campfire.AccountAsync();
        if (account.Logo is { Length: > 0 } && ImageSniff.ContentType(account.Logo) is { } type)
            return Results.Bytes(account.Logo, type);
        return StockLogo(size == "small");
    }

    private static IResult StockLogo(bool small)
    {
        var path = RailsAssets.LogoFile(small);
        return path is null ? Results.NotFound() : Results.File(path, "image/png");
    }

    private static async Task<IResult> UploadLogoAsync(HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync();
        var file = form.Files.GetFile("logo");
        if (file is null)
            return Results.BadRequest();
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        var type = ImageSniff.ContentType(bytes) ?? throw new AppException(422, "Logo must be a PNG, JPEG, GIF, or WebP image.");
        await campfire.SetLogoAsync(user.Id, bytes, type);
        return Results.Redirect("/account");
    }

    private static async Task<IResult> ClearLogoAsync(HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        await campfire.ClearLogoAsync(user.Id);
        return Results.Redirect("/account");
    }

    private static IResult QrAsync(string token)
    {
        var text = QrCode.Text(token);
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text, UriKind.Absolute, out _))
            return Results.BadRequest();
        return Results.Text(QrCode.Svg(text), "image/svg+xml; charset=utf-8");
    }

    private static async Task<IResult> AutocompleteAsync(long? room_id, string? query, string? filter, HttpContext http, CampfireApp campfire)
    {
        if (Current(http) is null)
            return Results.Unauthorized();
        var term = string.IsNullOrWhiteSpace(filter) ? query : filter;
        var users = await campfire.AutocompleteAsync(room_id, term);
        return Results.Json(users.Select(user => new { id = user.Id, name = user.Name, avatar = MessageMarkup.AvatarUrl(user) }));
    }

    private static async Task<IResult> UnfurlAsync(HttpContext http, ILinkFetcher fetcher, IAntiforgery antiforgery)
    {
        if (Current(http) is null)
            return Results.Unauthorized();
        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Text("Invalid authenticity token.", statusCode: StatusCodes.Status400BadRequest);
        }

        var form = await http.Request.ReadFormAsync();
        var url = form["url"].ToString();
        string? html;
        try
        {
            html = await fetcher.GetHtmlAsync(url, http.RequestAborted);
        }
        catch (Exception exception) when (exception is AppException or ViolationException or UnresolvableException)
        {
            return Results.NoContent();
        }

        if (html is null)
            return Results.NoContent();
        var preview = Unfurl.FromHtml(url, html);
        if (preview is null || !Unfurl.Publishable(preview, CampfireApp.SystemDns))
            return Results.NoContent();
        return Results.Json(new { title = preview.Title, url = preview.Url, image = preview.ImageUrl, description = preview.Description, html = preview.Html });
    }

    private static async Task<IResult> ManifestAsync(HttpContext http, CampfireApp campfire)
    {
        var name = "Campfire";
        if (await campfire.HasAccountAsync())
            name = (await campfire.AccountAsync()).Name;
        var manifest = new
        {
            name,
            icons = new object[]
            {
                new { src = "/account/logo?size=small", type = "image/png", sizes = "192x192" },
                new { src = "/account/logo", type = "image/png", sizes = "512x512" },
                new { src = "/account/logo", type = "image/png", sizes = "512x512", purpose = "maskable" },
            },
            start_url = "/",
            display = "standalone",
            scope = "/",
            description = "A chat app from the makers of Basecamp and HEY.",
            categories = new[] { "social", "business", "productivity" },
            theme_color = "#ffffff",
            background_color = "#ffffff",
            shortcuts = new object[]
            {
                new { name = "New chat room", description = "Open Campfire and start a new chat room", url = "/rooms/opens/new" },
                new { name = "My profile", description = "Open Campfire and view your profile", url = "/users/me/profile" },
            },
        };
        http.Response.ContentType = "application/manifest+json; charset=utf-8";
        return Results.Text(JsonSerializer.Serialize(manifest), "application/manifest+json; charset=utf-8");
    }

    private static async Task<IResult> TestPushAsync(HttpContext http, CampfireApp campfire, CampfireDb db)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        var subscriptions = db.PushSubscriptions.Where(item => item.UserId == user.Id).ToList();
        var outbound = http.RequestServices.GetRequiredService<IOutbound>();
        foreach (var subscription in subscriptions)
        {
            if (string.IsNullOrEmpty(subscription.P256dhKey) || string.IsNullOrEmpty(subscription.AuthKey))
                continue;
            outbound.EnqueuePush(new PushCall(subscription.Endpoint, subscription.P256dhKey, subscription.AuthKey, "Campfire", "Test notification", "/", 0));
        }
        await Task.CompletedTask;
        return Results.NoContent();
    }

    private static async Task<IResult> BotMessagesAsync(long id, string botKey, long? before, long? after, HttpContext http, CampfireApp campfire)
    {
        var bot = await campfire.AuthenticateBotAsync(botKey);
        if (bot is null)
            return Results.Unauthorized();
        if (!await campfire.IsMemberAsync(bot.Id, id))
            return Results.NotFound();
        if (HttpMethods.IsGet(http.Request.Method))
        {
            var page = await campfire.MessagesPageAsync(id, before, after);
            http.Response.Headers["X-Total-Count"] = (await campfire.MessageCountAsync(id)).ToString();
            if (page.Messages.Count > 0 && before is null && await campfire.HasOlderAsync(page.Messages))
                http.Response.Headers.Append("Link", $"<{http.Request.Path}?before={page.Messages[0].Id}>; rel=\"next\"");
            else if (page.Messages.Count > 0 && after is not null && await campfire.HasNewerAsync(page.Messages))
                http.Response.Headers.Append("Link", $"<{http.Request.Path}?after={page.Messages[^1].Id}>; rel=\"next\"");
            return Results.Json(page.Messages.Select(MessageJson));
        }

        var (body, file) = await ReadBotBodyAsync(http);
        if (string.IsNullOrWhiteSpace(body) && file is null)
            return Results.UnprocessableEntity();
        var message = await campfire.CreateMessageAsync(bot.Id, id, body, null, file, true);
        return Results.Created($"/rooms/{id}/@{message.Id}", MessageJson(message));
    }

    private static async Task<IResult> BotMessageItemAsync(long id, string botKey, long messageId, HttpContext http, CampfireApp campfire)
    {
        var bot = await campfire.AuthenticateBotAsync(botKey);
        if (bot is null)
            return Results.Unauthorized();
        if (HttpMethods.IsDelete(http.Request.Method))
        {
            await campfire.DeleteMessageAsync(bot.Id, id, messageId);
            return Results.NoContent();
        }

        var (body, _) = await ReadBotBodyAsync(http);
        var message = await campfire.UpdateMessageAsync(bot.Id, id, messageId, body);
        return Results.Json(MessageJson(message));
    }

    private static async Task<IResult> BotBoostAsync(long id, string botKey, long messageId, HttpContext http, CampfireApp campfire)
    {
        var bot = await campfire.AuthenticateBotAsync(botKey);
        if (bot is null)
            return Results.Unauthorized();
        if (HttpMethods.IsDelete(http.Request.Method))
        {
            await campfire.DeleteBoostAsync(bot.Id, id, messageId);
            return Results.NoContent();
        }

        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        var content = (await reader.ReadToEndAsync()).Trim();
        if (content.Length == 0)
            return Results.UnprocessableEntity();
        var boost = await campfire.CreateBoostAsync(bot.Id, id, messageId, content);
        return Results.Json(new { id = boost.Id, content = boost.Content }, statusCode: StatusCodes.Status201Created);
    }

    private static object MessageJson(Message message) => new
    {
        id = message.Id,
        creator_id = message.CreatorId,
        body = new { html = message.Html, plain = message.PlainText },
        path = "/rooms/" + message.RoomId + "/@" + message.Id,
        attachment = message.Attachment is null ? null : new { filename = message.Attachment.FileName, content_type = message.Attachment.ContentType },
    };

    private static async Task<(string Body, IncomingFile? File)> ReadBotBodyAsync(HttpContext http)
    {
        // curl -d 'Hello' posts the sentence as application/x-www-form-urlencoded. Rails uses that raw
        // body as the message. Only multipart carries a file; a urlencoded body is the message itself.
        http.Request.EnableBuffering();
        IncomingFile? file = null;
        var contentType = http.Request.ContentType ?? "";
        if (contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            var form = await http.Request.ReadFormAsync();
            file = await ReadFileAsync(form);
            var field = form["body"].ToString();
            if (!string.IsNullOrWhiteSpace(field) || file is not null)
                return (field, file);
        }

        http.Request.Body.Position = 0;
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = await reader.ReadToEndAsync();
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && text.TrimStart().StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("body", out var value)
                    && value.ValueKind == JsonValueKind.String)
                    return (value.GetString() ?? "", file);
            }
            catch (JsonException)
            {
            }
        }

        return (text, file);
    }

    private static async Task<IncomingFile?> ReadFileAsync(IFormCollection form)
    {
        var file = form.Files.GetFile("attachment");
        if (file is null || file.Length == 0)
            return null;
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        return new IncomingFile(file.FileName, file.ContentType, buffer.ToArray());
    }

    private const string ServiceWorker = """
        self.addEventListener("push", (event) => {
          event.waitUntil((async () => {
            const data = await event.data.json();
            await self.registration.showNotification(data.title, data.options);
            if (data.options && data.options.data && self.navigator.setAppBadge)
              await self.navigator.setAppBadge(data.options.data.badge || 0);
          })());
        });

        self.addEventListener("notificationclick", (event) => {
          event.notification.close();
          const path = event.notification.data && event.notification.data.path ? event.notification.data.path : "/";
          const url = new URL(path, self.location.origin).href;
          event.waitUntil((async () => {
            const clients = await self.clients.matchAll({ type: "window" });
            const focused = clients.find((client) => client.focused);
            if (focused)
              await focused.navigate(url);
            else
              await self.clients.openWindow(url);
          })());
        });
        """;
}
