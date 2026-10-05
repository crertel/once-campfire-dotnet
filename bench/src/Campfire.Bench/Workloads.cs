using System.Text.Json;

namespace Campfire.Bench;

public static class Workloads
{
    public static readonly string[] Names = ["room", "messages", "sidebar", "search"];

    public static IReadOnlyDictionary<string, string> All(JsonElement labels)
    {
        var room = Label(labels, "rooms.watercooler");
        var before = Label(labels, "messages.busy_060");
        return new Dictionary<string, string>
        {
            ["room"] = $"/rooms/{room}",
            ["messages"] = $"/rooms/{room}/messages?before={before}",
            ["sidebar"] = "/users/me/sidebar",
            ["search"] = "/searches?q=coffee",
        };
    }

    public static IReadOnlyDictionary<string, string> Select(JsonElement labels, string paths)
    {
        var requested = paths.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var all = All(labels);
        var selected = new Dictionary<string, string>();
        foreach (var name in requested)
        {
            if (!all.TryGetValue(name, out var path))
                throw new InvalidOperationException("--paths must select room,messages,sidebar,search");
            selected[name] = path;
        }

        if (selected.Count == 0 || selected.Count != requested.Length)
            throw new InvalidOperationException("--paths must select room,messages,sidebar,search");
        return selected;
    }

    public static string Label(JsonElement labels, string name)
    {
        if (!labels.TryGetProperty(name, out var value))
            throw new InvalidOperationException($"labels.json is missing {name}");

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? throw new InvalidOperationException($"labels.json {name} is null"),
            JsonValueKind.Number => value.GetRawText(),
            _ => throw new InvalidOperationException($"labels.json {name} must be a string or number"),
        };
    }
}
