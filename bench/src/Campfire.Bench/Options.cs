using System.Globalization;

namespace Campfire.Bench;

public sealed class OptionException(string message) : Exception(message);

public sealed class HelpException(string message) : Exception(message);

public static class OptionReader
{
    public static Dictionary<string, string?> Parse(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> defaults,
        string usage)
    {
        var options = new Dictionary<string, string?>(defaults);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
                throw new HelpException(usage);
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length < 3)
                throw new OptionException($"unexpected arguments: {string.Join(' ', args.Skip(i))}\n{usage}");

            var name = arg[2..].Replace('_', '-');
            if (i + 1 >= args.Count)
                throw new OptionException($"missing argument for --{name}\n{usage}");
            if (!options.ContainsKey(name))
                throw new OptionException($"unknown option --{name}\n{usage}");
            options[name] = args[++i];
        }

        return options;
    }

    public static string Required(Dictionary<string, string?> options, string name, string usage)
    {
        if (string.IsNullOrWhiteSpace(options[name]))
            throw new OptionException($"--baseline and --seed are required\n{usage}");
        return Path.GetFullPath(options[name]!);
    }

    public static int EvenRounds(string? text, string usage)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rounds) || rounds < 2 || rounds % 2 != 0)
            throw new OptionException($"--rounds must be even and at least 2\n{usage}");
        return rounds;
    }

    public static string Full(string? path) => Path.GetFullPath(path ?? throw new OptionException("missing path"));
}
