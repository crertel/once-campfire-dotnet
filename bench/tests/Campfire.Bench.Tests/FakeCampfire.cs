using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Campfire.Bench.Tests;

public enum FakeCampfireMode
{
    Normal,
    MissingCsrf,
    ServerError,
}

public sealed class FakeCampfire : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeCampfire(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public ConcurrentQueue<string> MeasuredEncodings { get; } = new();

    public static async Task<FakeCampfire> StartAsync(FakeCampfireMode mode = FakeCampfireMode.Normal)
    {
        var port = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        var host = new FakeCampfire(app, $"http://127.0.0.1:{port}");

        app.MapGet("/up", () => Results.Text("ok"));
        app.MapGet("/session/new", (HttpContext context) =>
        {
            context.Response.Headers.Append("Set-Cookie", "pre_session=abc; path=/");
            var token = mode == FakeCampfireMode.MissingCsrf ? "" : """<meta name="csrf-token" content="tok&amp;en">""";
            return Results.Content($"<html>{token}</html>", "text/html");
        });
        app.MapPost("/session", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            var cookie = context.Request.Headers.Cookie.ToString();
            if (!cookie.Contains("pre_session=abc", StringComparison.Ordinal))
                return Results.Text("missing cookie", statusCode: StatusCodes.Status400BadRequest);
            if (context.Request.Headers.Origin != $"{context.Request.Scheme}://{context.Request.Host}")
                return Results.Text("missing origin", statusCode: StatusCodes.Status400BadRequest);
            if (context.Request.Headers["Sec-Fetch-Site"] != "same-origin")
                return Results.Text("missing fetch site", statusCode: StatusCodes.Status400BadRequest);
            if (form["authenticity_token"] != "tok&en" || form["email_address"] != "david@example.com" || form["password"] != "secret")
                return Results.Text("bad credentials", statusCode: StatusCodes.Status401Unauthorized);

            context.Response.Headers.Append("Set-Cookie", "session_token=sess; path=/; httponly");
            return Results.Redirect("/");
        });
        app.MapGet("/rooms/{id}", (HttpContext context) =>
        {
            if (!context.Request.Headers.Cookie.ToString().Contains("session_token=sess", StringComparison.Ordinal))
                return Results.Unauthorized();

            var encoding = context.Request.Headers.AcceptEncoding.ToString();
            host.MeasuredEncodings.Enqueue(encoding);
            if (!encoding.Contains("identity", StringComparison.Ordinal))
                return Results.Text(encoding, statusCode: StatusCodes.Status415UnsupportedMediaType);
            if (mode == FakeCampfireMode.ServerError)
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            return Results.Text("hello");
        });

        await app.StartAsync();
        return host;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
