using System.Net;
using Campfire.Core;
using Campfire.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Campfire.Tests;

public sealed class HostingTests
{
    [Fact]
    public void Database_path_follows_the_rails_environment()
    {
        var read = new Dictionary<string, string?>();
        var path = CampfireStorage.DatabasePath(read: name => read.GetValueOrDefault(name));
        Assert.EndsWith($"{Path.DirectorySeparatorChar}storage{Path.DirectorySeparatorChar}db{Path.DirectorySeparatorChar}campfire.sqlite", path);

        read["RAILS_ENV"] = "production";
        path = CampfireStorage.DatabasePath(read: name => read.GetValueOrDefault(name));
        Assert.EndsWith($"{Path.DirectorySeparatorChar}storage{Path.DirectorySeparatorChar}db{Path.DirectorySeparatorChar}production.sqlite3", path);
        Assert.EndsWith($"{Path.DirectorySeparatorChar}storage{Path.DirectorySeparatorChar}backups{Path.DirectorySeparatorChar}production.sqlite3", CampfireStorage.BackupPath(read: name => read.GetValueOrDefault(name)));

        read["CAMPFIRE_DB"] = "custom.sqlite";
        path = CampfireStorage.DatabasePath(read: name => read.GetValueOrDefault(name));
        Assert.EndsWith($"{Path.DirectorySeparatorChar}custom.sqlite", path);
    }

    [Fact]
    public void Listen_url_uses_port_unless_aspnet_urls_are_set()
    {
        var read = new Dictionary<string, string?> { ["PORT"] = "25131" };
        Assert.Equal("http://0.0.0.0:25131", CampfireStorage.ListenUrl(name => read.GetValueOrDefault(name)));

        read["TARGET_BIND"] = "127.0.0.1";
        Assert.Equal("http://127.0.0.1:25131", CampfireStorage.ListenUrl(name => read.GetValueOrDefault(name)));

        read["ASPNETCORE_URLS"] = "http://127.0.0.1:3000";
        Assert.Null(CampfireStorage.ListenUrl(name => read.GetValueOrDefault(name)));
        Assert.Equal("Warning", CampfireStorage.RailsLogLevel(name => new Dictionary<string, string?> { ["RAILS_LOG_LEVEL"] = "warn" }.GetValueOrDefault(name)));
        Assert.True(CampfireStorage.SecureCookies(name => new Dictionary<string, string?> { ["TLS_DOMAIN"] = "chat.example.com" }.GetValueOrDefault(name)));
        Assert.False(CampfireStorage.SecureCookies(name => new Dictionary<string, string?> { ["TLS_DOMAIN"] = "chat.example.com", ["DISABLE_SSL"] = "true" }.GetValueOrDefault(name)));
    }

    [Fact]
    public void Secret_key_base_is_the_transfer_key()
    {
        var directory = Directory.CreateTempSubdirectory("campfire-secrets-");
        try
        {
            string? Read(string name) => name == "SECRET_KEY_BASE" ? "same-secret" : null;
            var first = AppSecrets.Load(directory.FullName, Read);
            var second = AppSecrets.Load(directory.FullName, Read);
            Assert.Equal(first.TransferKey, second.TransferKey);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "transfer.key")));
            var other = AppSecrets.Load(directory.FullName, name => name == "SECRET_KEY_BASE" ? "other-secret" : null);
            Assert.NotEqual(first.TransferKey, other.TransferKey);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Existing_rails_database_is_left_unchanged()
    {
        var directory = Directory.CreateTempSubdirectory("campfire-rails-db-");
        try
        {
            var database = Path.Combine(directory.FullName, "production.sqlite3");
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY)";
                await command.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<CampfireDb>().UseSqlite($"Data Source={database}").Options;
            await using var db = new CampfireDb(options);
            Assert.True(await db.EnsureReadyAsync());

            await using var check = new SqliteConnection($"Data Source={database}");
            await check.OpenAsync();
            await using var names = check.CreateCommand();
            names.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            var tables = new List<string>();
            await using var reader = await names.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
            Assert.Contains("users", tables);
            Assert.DoesNotContain("Users", tables);
            Assert.DoesNotContain("Attachments", tables);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Backup_copies_the_database()
    {
        var directory = Directory.CreateTempSubdirectory("campfire-backup-");
        try
        {
            var database = Path.Combine(directory.FullName, "db", "production.sqlite3");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE marks (id INTEGER)";
                await command.ExecuteNonQueryAsync();
                command.CommandText = "INSERT INTO marks (id) VALUES (7)";
                await command.ExecuteNonQueryAsync();
            }

            var destination = Path.Combine(directory.FullName, "backups", "production.sqlite3");
            DatabaseBackup.Create(database, destination);

            await using var copy = new SqliteConnection($"Data Source={destination}");
            await copy.OpenAsync();
            await using var read = copy.CreateCommand();
            read.CommandText = "SELECT id FROM marks";
            Assert.Equal(7, Convert.ToInt32(await read.ExecuteScalarAsync()));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Health_check_stays_up_on_a_rails_database()
    {
        var directory = Directory.CreateTempSubdirectory("campfire-rails-up-");
        try
        {
            var database = Path.Combine(directory.FullName, "db", "production.sqlite3");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY)";
                await command.ExecuteNonQueryAsync();
            }

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Campfire:Database"] = database,
            });
            var app = CampfireWeb.Build(builder);
            await app.StartAsync();
            try
            {
                var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var client = new HttpClient();
                using var response = await client.GetAsync(baseUrl + "/up");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("ok", (await response.Content.ReadAsStringAsync()).Trim());
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }

            await using var check = new SqliteConnection($"Data Source={database}");
            await check.OpenAsync();
            await using var names = check.CreateCommand();
            names.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY 1";
            var tables = new List<string>();
            await using var reader = await names.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
            Assert.Equal(new[] { "users" }, tables);
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
