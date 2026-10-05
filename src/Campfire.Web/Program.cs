using Campfire.Core;
using Campfire.Web;

if (args.Length > 0 && args[0] is "seed" or "hot-paths")
{
    Environment.ExitCode = await CampfireCli.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
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
