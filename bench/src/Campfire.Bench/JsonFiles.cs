using System.Text.Json;
using System.Text.Json.Nodes;

namespace Campfire.Bench;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static void Write(string path, JsonNode node)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, node.ToJsonString(Pretty) + "\n");
    }
}
