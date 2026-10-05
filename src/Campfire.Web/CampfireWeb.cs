using System.Data.Common;
using System.Net;
using System.Reflection;
using Campfire.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Campfire.Web;

public static class CampfireWeb
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LoginRateLimiter>();
        services.AddSingleton<LiveConnections>();
        services.AddSingleton<SqliteSetup>();
        services.AddSingleton<ICampfireRealtime, SignalRRealtime>();
        services.AddDbContext<CampfireDb>((serviceProvider, options) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var selected = configuration["Campfire:Database"]
                ?? Environment.GetEnvironmentVariable("CAMPFIRE_DB")
                ?? Path.Combine("storage", "db", "campfire.sqlite");
            var full = Path.GetFullPath(selected);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            options.UseSqlite($"Data Source={full}");
            options.AddInterceptors(serviceProvider.GetRequiredService<SqliteSetup>());
        });
        services.AddScoped<CampfireApp>();
        services.AddAntiforgery(options =>
        {
            options.FormFieldName = "authenticity_token";
            options.HeaderName = "X-CSRF-Token";
            options.Cookie.Name = "authenticity_token";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        var assembly = typeof(CampfireWeb).Assembly;
        services.AddRazorPages().ConfigureApplicationPartManager(manager => AddAssembly(manager, assembly));
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment());

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampfireDb>();
            db.Database.EnsureCreated();
        }

        if (app.Environment.IsDevelopment())
            app.UseDeveloperExceptionPage();

        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (AppException exception)
            {
                context.Response.StatusCode = exception.Status;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(exception.Message);
            }
        });
        app.UseMiddleware<BrowserMiddleware>();
        app.UseStaticFiles();
        app.UseMiddleware<SessionMiddleware>();
        app.UseAntiforgery();
        app.MapRazorPages();
        app.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();
        app.MapHub<CampfireHub>("/hubs/campfire");
        MapApi(app);
        return app;
    }

    private static void AddAssembly(ApplicationPartManager manager, Assembly assembly)
    {
        if (manager.ApplicationParts.Any(part => part.Name == assembly.GetName().Name))
            return;
        var factory = ApplicationPartFactory.GetApplicationPartFactory(assembly);
        foreach (var part in factory.GetApplicationParts(assembly))
            manager.ApplicationParts.Add(part);
    }

    private static void MapApi(WebApplication app)
    {
        app.MapGet("/up", () => Results.Text("ok"));

        app.MapPost("/session", LoginAsync).DisableAntiforgery();
        app.MapPost("/session/logout", LogoutAsync).DisableAntiforgery();
        app.MapDelete("/session", LogoutAsync).DisableAntiforgery();

        app.MapGet("/rooms/{id:long}/messages", MessagesAsync).DisableAntiforgery();
        app.MapPost("/rooms/{id:long}/messages", CreateMessageAsync).DisableAntiforgery();
        app.MapPost("/rooms/{id:long}/messages/{messageId:long}", UpdateMessageAsync).DisableAntiforgery();
        app.MapDelete("/rooms/{id:long}/messages/{messageId:long}", DeleteMessageAsync).DisableAntiforgery();
        app.MapPost("/rooms/{id:long}/messages/{messageId:long}/boosts", CreateBoostAsync).DisableAntiforgery();
        app.MapDelete("/rooms/{id:long}/messages/{messageId:long}/boosts", DeleteBoostAsync).DisableAntiforgery();
        app.MapPost("/rooms/{id:long}/draft", SaveDraftAsync).DisableAntiforgery();
        app.MapPost("/rooms/{id:long}/{botKey}/messages", BotMessageAsync).DisableAntiforgery();

        app.MapPost("/users/{id:long}/ban", BanAsync).DisableAntiforgery();
        app.MapPost("/users/{id:long}/unban", UnbanAsync).DisableAntiforgery();
        app.MapDelete("/users/{id:long}/ban", UnbanAsync).DisableAntiforgery();
        app.MapGet("/users/{id:long}/avatar", AvatarAsync).DisableAntiforgery();
        app.MapPost("/users/{id:long}/avatar", UploadAvatarAsync).DisableAntiforgery();
        app.MapDelete("/users/{id:long}/avatar", ClearAvatarAsync).DisableAntiforgery();
        app.MapPost("/account/bots", CreateBotAsync).DisableAntiforgery();
        app.MapPost("/account/bots/{id:long}/key", ResetBotAsync).DisableAntiforgery();
        app.MapPost("/users/me/push_subscriptions", PushAsync).DisableAntiforgery();
    }

    private static async Task<IResult> LoginAsync(HttpContext http, CampfireApp campfire, IAntiforgery antiforgery, LoginRateLimiter limiter)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Text("Invalid authenticity token.", statusCode: StatusCodes.Status400BadRequest);
        }

        var form = await http.Request.ReadFormAsync();
        var email = form["email_address"].ToString();
        var password = form["password"].ToString();
        var key = string.IsNullOrEmpty(email) ? http.Connection.RemoteIpAddress?.ToString() ?? "unknown" : email;
        if (!limiter.TryAcquire(key))
            return Results.Text("Too many requests or unauthorized.", statusCode: StatusCodes.Status429TooManyRequests);

        var session = await campfire.LoginAsync(email, password, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
        if (session is null)
            return Results.Text("Too many requests or unauthorized.", statusCode: StatusCodes.Status401Unauthorized);

        http.Response.Cookies.Append(SessionCookies.Name, session.Token, SessionCookies.Append);
        return Results.Redirect("/");
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, CampfireApp campfire)
    {
        var endpoint = http.Request.Query["push_subscription_endpoint"].ToString();
        if (string.IsNullOrEmpty(endpoint) && http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync();
            endpoint = form["push_subscription_endpoint"].ToString();
        }

        await campfire.LogoutAsync(http.Request.Cookies[SessionCookies.Name], string.IsNullOrEmpty(endpoint) ? null : endpoint);
        http.Response.Cookies.Delete(SessionCookies.Name, SessionCookies.Delete);
        return Results.Redirect("/");
    }

    private static async Task<IResult> MessagesAsync(long id, long? before, long? after, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Redirect("/session/new");
        if (!await campfire.IsMemberAsync(user.Id, id))
            return Results.NotFound();

        var page = await campfire.MessagesPageAsync(id, before, after);
        if (!page.RoomHasMessages)
            return Results.NoContent();
        return Results.Content(Fragments.Messages(page.Messages), "text/html; charset=utf-8");
    }

    private static async Task<IResult> CreateMessageAsync(long id, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync();
        var body = form["body"].ToString();
        var clientId = form["client_message_id"].ToString();
        var message = await campfire.CreateMessageAsync(user.Id, id, body, string.IsNullOrEmpty(clientId) ? null : clientId);
        return Results.Redirect($"/rooms/{id}");
    }

    private static async Task<IResult> UpdateMessageAsync(long id, long messageId, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync();
        await campfire.UpdateMessageAsync(user.Id, id, messageId, form["body"].ToString());
        return Results.Redirect($"/rooms/{id}");
    }

    private static async Task<IResult> DeleteMessageAsync(long id, long messageId, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        await campfire.DeleteMessageAsync(user.Id, id, messageId);
        return Results.Redirect($"/rooms/{id}");
    }

    private static async Task<IResult> CreateBoostAsync(long id, long messageId, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        var content = http.Request.HasFormContentType
            ? (await http.Request.ReadFormAsync())["content"].ToString()
            : "";
        var boost = await campfire.CreateBoostAsync(user.Id, id, messageId, content);
        return Results.Text(boost.Content);
    }

    private static async Task<IResult> DeleteBoostAsync(long id, long messageId, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        await campfire.DeleteBoostAsync(user.Id, id, messageId);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveDraftAsync(long id, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync();
        await campfire.SaveDraftAsync(user.Id, id, form["body"].ToString());
        return Results.NoContent();
    }

    private static async Task<IResult> BotMessageAsync(long id, string botKey, HttpContext http, CampfireApp campfire)
    {
        var bot = await campfire.AuthenticateBotAsync(botKey);
        if (bot is null)
            return Results.Unauthorized();
        using var document = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body);
        var body = document.RootElement.TryGetProperty("body", out var value) ? value.GetString() ?? "" : "";
        var message = await campfire.CreateMessageAsync(bot.Id, id, body, null);
        return Results.Json(new { id = message.Id, body = message.PlainText });
    }

    private static async Task<IResult> BanAsync(long id, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        await campfire.BanAsync(user.Id, id);
        return Results.Redirect($"/users/{id}");
    }

    private static async Task<IResult> UnbanAsync(long id, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        await campfire.UnbanAsync(user.Id, id);
        return Results.Redirect($"/users/{id}");
    }

    private static async Task<IResult> AvatarAsync(long id, HttpContext http, CampfireDb db)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id);
        var contentType = user?.Avatar is null ? null : ImageSniff.ContentType(user.Avatar);
        if (user?.Avatar is null || contentType is null)
            return Results.NotFound();
        http.Response.Headers.Append("X-Content-Type-Options", "nosniff");
        http.Response.Headers.Append("Content-Disposition", "inline");
        return Results.Bytes(user.Avatar, contentType);
    }

    private static async Task<IResult> UploadAvatarAsync(long id, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null || (user.Id != id && !user.IsAdministrator))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!http.Request.HasFormContentType)
            return Results.BadRequest();
        var form = await http.Request.ReadFormAsync();
        var file = form.Files.GetFile("avatar");
        if (file is null || file.Length == 0)
            return Results.BadRequest();
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        await campfire.SetAvatarAsync(id, buffer.ToArray());
        return Results.Redirect($"/users/{id}");
    }

    private static async Task<IResult> ClearAvatarAsync(long id, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null || (user.Id != id && !user.IsAdministrator))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        await campfire.ClearAvatarAsync(id);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateBotAsync(HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync();
        var bot = await campfire.CreateBotAsync(user.Id, form["name"].ToString(), form["webhook_url"].ToString());
        return Results.Text(CampfireApp.BotKey(bot));
    }

    private static async Task<IResult> ResetBotAsync(long id, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        var bot = await campfire.ResetBotKeyAsync(user.Id, id);
        return Results.Text(CampfireApp.BotKey(bot));
    }

    private static async Task<IResult> PushAsync(HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync();
        await campfire.AddPushSubscriptionAsync(
            user.Id,
            form["endpoint"].ToString(),
            form["p256dh"].ToString(),
            form["auth"].ToString(),
            http.Request.Headers.UserAgent.ToString(),
            CampfireApp.SystemDns);
        return Results.NoContent();
    }

    private static User? Current(HttpContext http) => http.Items["User"] as User;
}

public sealed class SqliteSetup : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
    }
}

public static class Fragments
{
    public static string Messages(IEnumerable<Message> messages)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var message in messages)
        {
            builder.Append("<article class=\"message\" id=\"message-").Append(message.Id).Append("\">");
            builder.Append("<p class=\"author\">").Append(WebUtility.HtmlEncode(message.Creator?.Name ?? "")).Append("</p>");
            builder.Append("<div class=\"body\">").Append(message.Html).Append("</div>");
            foreach (var boost in message.Boosts.OrderBy(item => item.CreatedAt))
                builder.Append("<span class=\"boost\">").Append(WebUtility.HtmlEncode(boost.Content)).Append("</span>");
            builder.Append("</article>");
        }
        return builder.ToString();
    }
}

public sealed class BrowserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!BrowserPolicy.Allowed(context.Request.Headers.UserAgent.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("""
                <!DOCTYPE html>
                <html><head><title>Upgrade your browser</title></head>
                <body><h1>Upgrade to a supported web browser</h1></body></html>
                """);
            return;
        }

        await next(context);
    }
}

public sealed class SessionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CampfireApp campfire)
    {
        var user = await campfire.UserFromTokenAsync(context.Request.Cookies[SessionCookies.Name]);
        if (user is not null)
            context.Items["User"] = user;
        if (await campfire.HasAccountAsync())
            context.Items["Account"] = await campfire.AccountAsync();
        await next(context);
    }
}
