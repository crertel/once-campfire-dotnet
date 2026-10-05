using System.Text.RegularExpressions;

namespace Campfire.Core;

public static partial class BrowserPolicy
{
    public static bool Allowed(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return true;

        if (Ie().IsMatch(userAgent))
            return false;
        if (Firefox().IsMatch(userAgent))
            return AtLeast(userAgent, Firefox(), 121, 0);
        if (Edge().IsMatch(userAgent))
            return AtLeast(userAgent, Edge(), 120, 0);
        if (Opera().IsMatch(userAgent))
            return AtLeast(userAgent, Opera(), 104, 0);
        if (Chrome().IsMatch(userAgent))
            return AtLeast(userAgent, Chrome(), 120, 0);
        if (userAgent.Contains("Safari", StringComparison.Ordinal))
            return AtLeast(userAgent, SafariVersion(), 17, 2);
        return true;
    }

    private static bool AtLeast(string userAgent, Regex token, int major, int minor)
    {
        var match = token.Match(userAgent);
        if (!match.Success)
            return false;
        var foundMajor = int.Parse(match.Groups[1].Value);
        var foundMinor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        if (foundMajor != major)
            return foundMajor > major;
        return foundMinor >= minor;
    }

    [GeneratedRegex(@"MSIE|Trident", RegexOptions.CultureInvariant)]
    private static partial Regex Ie();

    [GeneratedRegex(@"Firefox/(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex Firefox();

    [GeneratedRegex(@"Edg/(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex Edge();

    [GeneratedRegex(@"OPR/(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex Opera();

    [GeneratedRegex(@"Chrome/(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex Chrome();

    [GeneratedRegex(@"Version/(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex SafariVersion();
}
