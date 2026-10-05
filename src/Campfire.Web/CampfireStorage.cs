namespace Campfire.Web;

// Paths and environment the Rails image and the shootout harness already use.
public static class CampfireStorage
{
    public static string DatabasePath(IConfiguration? configuration = null, Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var selected = configuration?["Campfire:Database"];
        if (string.IsNullOrWhiteSpace(selected))
            selected = read("CAMPFIRE_DB");
        if (string.IsNullOrWhiteSpace(selected))
        {
            var railsEnv = read("RAILS_ENV");
            selected = string.IsNullOrWhiteSpace(railsEnv)
                ? Path.Combine("storage", "db", "campfire.sqlite")
                : Path.Combine("storage", "db", $"{railsEnv}.sqlite3");
        }

        return Path.GetFullPath(selected);
    }

    public static string BackupPath(IConfiguration? configuration = null, Func<string, string?>? read = null)
    {
        var database = DatabasePath(configuration, read);
        var directory = Path.GetDirectoryName(database) ?? Path.GetFullPath("storage");
        var storage = Path.GetFullPath(Path.Combine(directory, ".."));
        return Path.Combine(storage, "backups", Path.GetFileName(database));
    }

    // Thruster listens on HTTP_PORT and sets PORT to TARGET_PORT for this process.
    // A configured ASPNETCORE_URLS stays in charge, which is how nix run starts Kestrel.
    public static string? ListenUrl(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(read("ASPNETCORE_URLS")))
            return null;
        var port = read("PORT");
        if (string.IsNullOrWhiteSpace(port))
            return null;
        var bind = read("TARGET_BIND");
        if (string.IsNullOrWhiteSpace(bind))
            bind = "0.0.0.0";
        return $"http://{bind}:{port}";
    }

    public static string? RailsLogLevel(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        if (!string.IsNullOrWhiteSpace(read("Logging__LogLevel__Default")))
            return null;
        return read("RAILS_LOG_LEVEL")?.Trim().ToLowerInvariant() switch
        {
            "debug" => "Debug",
            "info" => "Information",
            "warn" or "warning" => "Warning",
            "error" or "fatal" => "Error",
            _ => null,
        };
    }

    // TLS_DOMAIN makes Thruster terminate HTTPS. DISABLE_SSL is the Rails switch for plain HTTP.
    public static bool SecureCookies(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        return string.IsNullOrWhiteSpace(read("DISABLE_SSL"))
            && !string.IsNullOrWhiteSpace(read("TLS_DOMAIN"));
    }
}
