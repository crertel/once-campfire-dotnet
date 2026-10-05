using System.Net;
using System.Text.RegularExpressions;

namespace Campfire.Core;

public sealed record LinkPreview(string Url, string? Title, string? Description, string? ImageUrl)
{
    public string Html
    {
        get
        {
            var title = WebUtility.HtmlEncode(Title ?? "");
            var description = WebUtility.HtmlEncode(Description ?? "");
            var image = ImageUrl is null
                ? ""
                : $"<div class=\"og-embed__image\"><img src=\"{WebUtility.HtmlEncode(ImageUrl)}\" class=\"og-embed__img\" alt=\"\"></div>";
            return $"<article class=\"og-embed\"><h2 class=\"og-embed__title\">{title}</h2><p class=\"og-embed__description\">{description}</p>{image}</article>";
        }
    }
}

public static partial class Unfurl
{
    private static readonly string[] MediaExtensions =
    [
        ".zip", ".tar", ".gz", ".bz2", ".rar", ".7z", ".dmg", ".exe", ".msi", ".pkg", ".deb", ".iso",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".flv",
        ".heic", ".heif", ".mp3", ".wav", ".ogg", ".aac", ".wma", ".webm", ".ogv", ".mpg", ".mpeg",
    ];

    public static bool ShouldFetch(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https"))
            return false;
        var path = uri.AbsolutePath.ToLowerInvariant();
        return !MediaExtensions.Any(path.EndsWith);
    }

    public static LinkPreview? FromHtml(string pageUrl, string html)
    {
        string? title = null;
        string? description = null;
        string? image = null;
        foreach (Match match in Meta().Matches(html))
        {
            var tag = match.Value;
            var property = Attr(tag, "property") ?? Attr(tag, "name");
            var content = Attr(tag, "content");
            if (property is null || content is null)
                continue;
            switch (property.ToLowerInvariant())
            {
                case "og:title":
                    title ??= content;
                    break;
                case "og:description":
                    description ??= content;
                    break;
                case "og:image":
                    image ??= content;
                    break;
            }
        }

        if (title is null && description is null && image is null)
            return null;
        return new LinkPreview(pageUrl, title, description, image);
    }

    public static bool Publishable(LinkPreview preview, Func<string, IReadOnlyList<IPAddress>> dns)
    {
        if (string.IsNullOrWhiteSpace(preview.Title) || string.IsNullOrWhiteSpace(preview.Description) || string.IsNullOrWhiteSpace(preview.Url))
            return false;
        try
        {
            EnsurePublic(preview.Url, dns);
            if (!string.IsNullOrWhiteSpace(preview.ImageUrl))
                EnsurePublic(preview.ImageUrl, dns);
        }
        catch (Exception exception) when (exception is AppException or ViolationException or UnresolvableException)
        {
            return false;
        }
        return true;
    }

    public static void EnsurePublic(string url, Func<string, IReadOnlyList<IPAddress>> dns)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new AppException(422, "url is invalid");
        _ = PrivateNetwork.Resolve(uri.IdnHost, dns);
    }

    private static string? Attr(string tag, string name)
    {
        var match = Regex.Match(
            tag,
            name + """\s*=\s*(?:"([^"]*)"|'([^']*)')""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;
        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return WebUtility.HtmlDecode(value);
    }

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Meta();
}
