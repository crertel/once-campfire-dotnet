using System.Security.Cryptography;
using Campfire.Core;
using Campfire.Web;

if (args.Length > 0 && args[0] is "seed" or "hot-paths" or "secrets" or "backup")
{
    Environment.ExitCode = await CampfireCli.RunAsync(args);
    return;
}

var logLevel = CampfireStorage.RailsLogLevel();
if (logLevel is not null)
    Environment.SetEnvironmentVariable("Logging__LogLevel__Default", logLevel);

// The ASP.NET container image defaults to port 8080. Thruster owns HTTP_PORT and
// tells this process the upstream port through PORT.
var listen = CampfireStorage.ListenUrl();
if (listen is not null)
{
    Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", null);
    Environment.SetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS", null);
}

var builder = WebApplication.CreateBuilder(args);
if (listen is not null)
    builder.WebHost.UseUrls(listen);
var app = CampfireWeb.Build(builder);
app.Run();

public partial class Program;

public static class CampfireCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            if (args[0] == "seed")
            {
                var output = Value(args, "--output") ?? throw new InvalidOperationException("seed requires --output DIR");
                await Seeder.WriteAsync(output, cancellationToken);
                Console.WriteLine(output);
                return 0;
            }

            if (args[0] == "hot-paths")
            {
                var database = Value(args, "--db") ?? throw new InvalidOperationException("hot-paths requires --db and --output");
                var output = Value(args, "--output") ?? throw new InvalidOperationException("hot-paths requires --db and --output");
                var json = await HotPaths.MeasureAsync(database, cancellationToken);
                await File.WriteAllTextAsync(output, json, cancellationToken);
                Console.WriteLine(output);
                return 0;
            }

            if (args[0] == "secrets")
            {
                var (publicKey, privateKey) = WebPush.GenerateVapid();
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(64)).ToLowerInvariant();
                Console.WriteLine($"SECRET_KEY_BASE={secret}");
                Console.WriteLine($"VAPID_PRIVATE_KEY={privateKey}");
                Console.WriteLine($"VAPID_PUBLIC_KEY={publicKey}");
                return 0;
            }

            if (args[0] == "backup")
            {
                var database = CampfireStorage.DatabasePath();
                var destination = CampfireStorage.BackupPath();
                DatabaseBackup.Create(database, destination);
                Console.WriteLine(destination);
                return 0;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        return 1;
    }

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }
}
