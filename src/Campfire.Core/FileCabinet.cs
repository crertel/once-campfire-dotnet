namespace Campfire.Core;

public sealed class FileCabinet
{
    public const int MaxBytes = 20 * 1024 * 1024;

    public FileCabinet(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public async Task<string> SaveAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes)
            throw new AppException(422, "That file is empty or larger than 20 MB.");
        var key = Guid.NewGuid().ToString("n");
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return key;
    }

    public string PathFor(string key)
    {
        var path = Path.GetFullPath(Path.Combine(Root, key));
        var root = Path.GetFullPath(Root);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && path != root)
            throw new AppException(404, "File not found.");
        return path;
    }

    public byte[]? Read(string key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public void Delete(string key)
    {
        var path = PathFor(key);
        if (File.Exists(path))
            File.Delete(path);
    }
}

public sealed record IncomingFile(string FileName, string ContentType, byte[] Bytes);
