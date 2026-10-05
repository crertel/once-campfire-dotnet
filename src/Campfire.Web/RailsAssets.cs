using System.Text.RegularExpressions;

namespace Campfire.Web;

public static partial class RailsAssets
{
    public static string Stylesheet() => Sheet.Value;

    public static string? ImageRoot()
    {
        var root = FindRoot();
        return root is null ? null : Path.Combine(root, "images");
    }

    private static readonly Lazy<string> Sheet = new(BuildSheet);

    private static string BuildSheet()
    {
        var root = FindRoot() ?? throw new InvalidOperationException("Rails assets were not found.");
        var styles = Path.Combine(root, "stylesheets");
        var files = Directory.GetFiles(styles, "*.css").OrderBy(Path.GetFileName, StringComparer.Ordinal);
        var text = string.Join('\n', files.Select(File.ReadAllText));
        return Url().Replace(text, match =>
        {
            var path = match.Groups["path"].Value.Trim();
            if (path.StartsWith('/') || path.StartsWith('#') || path.Contains(':'))
                return match.Value;
            while (path.StartsWith("./", StringComparison.Ordinal))
                path = path[2..];
            return "url(/assets/images/" + path + ")";
        });
    }

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "app", "assets");
            if (Directory.Exists(Path.Combine(candidate, "stylesheets")) && Directory.Exists(Path.Combine(candidate, "images")))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    [GeneratedRegex(@"url\(\s*[""']?(?<path>[^""')]+)[""']?\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Url();
}
