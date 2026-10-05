using System.Data.Common;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using Campfire.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;

namespace Campfire.Web;

public static partial class CampfireWeb
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
        services.AddSingleton(static serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var selected = configuration["Campfire:Database"]
                ?? Environment.GetEnvironmentVariable("CAMPFIRE_DB")
                ?? Path.Combine("storage", "db", "campfire.sqlite");
            var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(selected)) ?? Path.GetFullPath("storage");
            return new FileCabinet(Path.GetFullPath(Path.Combine(databaseDirectory, "..", "files")));
        });
        services.AddSingleton(static serviceProvider => AppSecrets.Load(Path.GetFullPath(Path.Combine(serviceProvider.GetRequiredService<FileCabinet>().Root, ".."))));
        services.AddSingleton<DeliveryQueue>();
        services.AddSingleton<IOutbound>(static serviceProvider => serviceProvider.GetRequiredService<DeliveryQueue>());
        services.AddHostedService(static serviceProvider => serviceProvider.GetRequiredService<DeliveryQueue>());
        services.AddHttpClient("webhooks");
        services.AddHttpClient<ILinkFetcher, GuardedLinkFetcher>()
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { ConnectCallback = ConnectPublic });
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
        services.AddRazorPages(options =>
        {
            options.Conventions.AddPageRoute("/Room", @"/rooms/{id:long}/@{messageId:long}");
        }).ConfigureApplicationPartManager(manager => AddAssembly(manager, assembly));
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddSignalR(options => options.EnableDetailedErrors = builder.Environment.IsDevelopment());

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampfireDb>();
            db.EnsureReadyAsync().GetAwaiter().GetResult();
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
        var images = RailsAssets.ImageRoot();
        if (images is not null)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(images),
                RequestPath = "/assets/images",
            });
        }

        var sounds = RailsAssets.SoundRoot();
        if (sounds is not null)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(sounds),
                RequestPath = "/assets/sounds",
            });
        }
        var soundImages = RailsAssets.SoundImageRoot();
        if (soundImages is not null)
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(soundImages),
                RequestPath = "/assets/images/sounds",
            });
        }

        app.UseMiddleware<SessionMiddleware>();
        app.UseAntiforgery();
        app.MapGet("/assets/application.css", () => Results.Text(RailsAssets.Stylesheet(), "text/css; charset=utf-8"));
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
        app.MapMethods("/rooms/{id:long}/{botKey}/messages/{messageId:long}/boosts", [HttpMethods.Post, HttpMethods.Delete], BotBoostAsync).DisableAntiforgery();
        app.MapMethods("/rooms/{id:long}/{botKey}/messages/{messageId:long}", [HttpMethods.Put, HttpMethods.Post, HttpMethods.Delete], BotMessageItemAsync).DisableAntiforgery();
        app.MapMethods("/rooms/{id:long}/{botKey}/messages", [HttpMethods.Get, HttpMethods.Post], BotMessagesAsync).DisableAntiforgery();

        app.MapPost("/users/{id:long}/ban", BanAsync).DisableAntiforgery();
        app.MapPost("/users/{id:long}/unban", UnbanAsync).DisableAntiforgery();
        app.MapDelete("/users/{id:long}/ban", UnbanAsync).DisableAntiforgery();
        app.MapGet("/users/{id:long}/avatar", AvatarAsync).DisableAntiforgery();
        app.MapPost("/users/{id:long}/avatar", UploadAvatarAsync).DisableAntiforgery();
        app.MapDelete("/users/{id:long}/avatar", ClearAvatarAsync).DisableAntiforgery();
        app.MapPost("/account/bots", CreateBotAsync).DisableAntiforgery();
        app.MapPost("/account/bots/{id:long}/key", ResetBotAsync).DisableAntiforgery();
        app.MapPost("/users/me/push_subscriptions", PushAsync).DisableAntiforgery();
        app.MapPost("/users/me/push_subscriptions/test", TestPushAsync).DisableAntiforgery();
        MapProduct(app);
    }

    private static async ValueTask<Stream> ConnectPublic(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var address = PrivateNetwork.Resolve(context.DnsEndPoint.Host, CampfireApp.SystemDns);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
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
        return Results.Content(Fragments.Messages(page.Messages, user.Id), "text/html; charset=utf-8");
    }

    private static async Task<IResult> CreateMessageAsync(long id, HttpContext http, CampfireApp campfire)
    {
        var user = Current(http);
        if (user is null)
            return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync();
        var body = form["body"].ToString();
        if (!string.IsNullOrEmpty(form["unfurl_html"]))
            body += form["unfurl_html"].ToString();
        var clientId = form["client_message_id"].ToString();
        var file = await ReadFileAsync(form);
        var message = await campfire.CreateMessageAsync(user.Id, id, body, string.IsNullOrEmpty(clientId) ? null : clientId, file, true);
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
    public static string Messages(IEnumerable<Message> messages, long viewerId)
    {
        var builder = new System.Text.StringBuilder();
        Message? previous = null;
        foreach (var message in messages)
        {
            builder.Append(MessageMarkup.One(message, message.Room?.Name ?? "", previous, viewerId));
            previous = message;
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
            await context.Response.WriteAsync(UnsupportedBrowserPage.Html);
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
