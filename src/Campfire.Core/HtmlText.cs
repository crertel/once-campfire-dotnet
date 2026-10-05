using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Campfire.Core;

public static partial class HtmlText
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "br", "p", "div", "span", "strong", "em", "del", "s", "strike", "u", "mark",
        "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "code",
        "ul", "ol", "li", "table", "thead", "tbody", "tr", "td", "th", "figure",
    };

    public static string Compose(string input, IReadOnlyList<User> members, string? campfireHost)
    {
        var html = input.Contains('<', StringComparison.Ordinal)
            ? input
            : WebUtility.HtmlEncode(input).Replace("\r\n", "\n").Replace("\n", "<br>");
        html = ApplyMentions(html, members);
        return Sanitize(html, campfireHost);
    }

    public static string ToPlain(string html)
    {
        var stripped = Tag().Replace(html, " ");
        var decoded = WebUtility.HtmlDecode(stripped);
        return Whitespace().Replace(decoded, " ").Trim();
    }

    public static string Stem(string word)
    {
        if (word.EndsWith("sses", StringComparison.Ordinal) && word.Length >= 4)
            return word[..^2];
        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
            return word[..^1];
        return word;
    }

    public static IReadOnlyList<string> Terms(string plain)
    {
        var terms = new List<string>();
        foreach (var raw in Word().Split(plain.ToLowerInvariant()))
        {
            if (raw.Length == 0)
                continue;
            terms.Add(Stem(raw));
        }
        return terms;
    }

    public static string Sanitize(string html, string? campfireHost)
    {
        if (string.IsNullOrEmpty(html))
            return "";

        var withoutBlocks = ScriptBlock().Replace(html, "");
        withoutBlocks = StyleBlock().Replace(withoutBlocks, "");
        if (IsClean(withoutBlocks, campfireHost))
            return withoutBlocks;
        return RebuildSimple(withoutBlocks, campfireHost);
    }

    public static string ApplyMentions(string html, IReadOnlyList<User> members)
    {
        if (members.Count == 0 || !html.Contains('@'))
            return html;

        var parts = MentionSpan().Split(html);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("<span class=\"mention\"", StringComparison.Ordinal))
                continue;
            foreach (var member in members.OrderByDescending(user => user.Name.Length))
            {
                if (member.Name.Length == 0)
                    continue;
                var pattern = $@"(?<![\w@])@{Regex.Escape(member.Name)}\b";
                var encoded = WebUtility.HtmlEncode(member.Name);
                parts[i] = Regex.Replace(
                    parts[i],
                    pattern,
                    $"<span class=\"mention\" data-user-id=\"{member.Id}\">@{encoded}</span>");
            }
        }
        return string.Concat(parts);
    }

    private static bool IsClean(string html, string? campfireHost)
    {
        foreach (Match tag in AnyTag().Matches(html))
        {
            var name = tag.Groups["name"].Value;
            if (!AllowedTags.Contains(name))
                return false;
            foreach (var attribute in Attributes(tag.Groups["attrs"].Value))
            {
                if (!AttributeAllowed(name, attribute.Name, attribute.Value, campfireHost))
                    return false;
            }
        }
        return true;
    }

    private static string RebuildSimple(string html, string? campfireHost)
    {
        var builder = new StringBuilder();
        var index = 0;
        foreach (Match tag in AnyTag().Matches(html))
        {
            builder.Append(html, index, tag.Index - index);
            index = tag.Index + tag.Length;
            var name = tag.Groups["name"].Value;
            var closing = tag.Value.StartsWith("</", StringComparison.Ordinal);
            if (!AllowedTags.Contains(name))
                continue;

            if (closing)
            {
                builder.Append("</").Append(name).Append('>');
                continue;
            }

            builder.Append('<').Append(name);
            foreach (var attribute in Attributes(tag.Groups["attrs"].Value))
            {
                if (AttributeAllowed(name, attribute.Name, attribute.Value, campfireHost))
                    builder.Append(' ').Append(attribute.Raw);
            }
            if (tag.Value.EndsWith("/>", StringComparison.Ordinal))
                builder.Append(" />");
            else
                builder.Append('>');
        }
        builder.Append(html, index, html.Length - index);
        return builder.ToString();
    }

    private static bool AttributeAllowed(string tag, string name, string value, string? campfireHost)
    {
        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            return false;
        if (name.Equals("class", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Equals("data-language", StringComparison.OrdinalIgnoreCase))
            return true;
        if (name.Equals("data-user-id", StringComparison.OrdinalIgnoreCase) && tag.Equals("span", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(value, out _);
        if (!name.Equals("href", StringComparison.OrdinalIgnoreCase))
            return false;
        return HrefAllowed(value, campfireHost);
    }

    public static bool HrefAllowed(string href, string? campfireHost)
    {
        if (string.IsNullOrWhiteSpace(href))
            return false;
        var trimmed = href.Trim();
        if (trimmed.StartsWith('/') && !trimmed.StartsWith("//", StringComparison.Ordinal))
            return true;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https"))
            return false;
        if (IPAddress.TryParse(uri.IdnHost, out _))
            return false;
        if (!string.IsNullOrEmpty(campfireHost) && uri.IdnHost.Equals(campfireHost, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static IEnumerable<(string Name, string Value, string Raw)> Attributes(string source)
    {
        foreach (Match match in Attribute().Matches(source))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["dq"].Success
                ? match.Groups["dq"].Value
                : match.Groups["sq"].Success
                    ? match.Groups["sq"].Value
                    : match.Groups["bare"].Value;
            yield return (name, WebUtility.HtmlDecode(value), match.Value.Trim());
        }
    }

    [GeneratedRegex(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptBlock();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex StyleBlock();

    [GeneratedRegex(@"</?(?<name>[a-zA-Z][\w:-]*)\b(?<attrs>[^>]*)>", RegexOptions.CultureInvariant)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex("""(<span class="mention"[^>]*>.*?</span>)""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex MentionSpan();

    [GeneratedRegex("""
        (?<name>[a-zA-Z_:][\w:.-]*)\s*=\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)'|(?<bare>[^\s"'>]+))
        """, RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();
}
