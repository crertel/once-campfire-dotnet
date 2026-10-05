namespace Campfire.Bench;

public static class StorageLayout
{
    public static void Prepare(string seed, string destination)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        CopyTree(Path.Combine(seed, "db"), Path.Combine(destination, "db"));
        CopyTree(Path.Combine(seed, "storage"), Path.Combine(destination, "files"));
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
                CopyTree(entry, target);
            else
                File.Copy(entry, target, overwrite: true);
        }
    }
}
