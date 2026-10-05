using Microsoft.Data.Sqlite;

namespace Campfire.Core;

public static class DatabaseBackup
{
    public static void Create(string databasePath, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        source.Open();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }
}
