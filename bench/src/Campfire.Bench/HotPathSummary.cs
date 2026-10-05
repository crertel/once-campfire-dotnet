using System.Text.Json.Nodes;

namespace Campfire.Bench;

public static class HotPathSummary
{
    private static readonly string[] ParityFields = ["body_sha256", "headers", "payload_sha256"];
    private static readonly string[] MeasuredFields = ["milliseconds", "allocations", "queries"];

    public static JsonObject Results(IReadOnlyList<JsonObject> before, IReadOnlyList<JsonObject> after)
    {
        if (before.Count == 0 || after.Count == 0)
            throw new InvalidOperationException("hot-path comparison needs at least one before run and one after run");

        var summary = new JsonObject();
        foreach (var name in ResultNames(before[0]))
        {
            var beforeRows = Rows(before, name);
            var afterRows = Rows(after, name);
            foreach (var field in ParityFields)
            {
                var expected = Field(beforeRows[0], field);
                if (!beforeRows.Concat(afterRows).All(row => JsonNode.DeepEquals(expected, Field(row, field))))
                    throw new InvalidOperationException($"{name}: {field} differs; no winning summary written");
            }

            var compared = new JsonObject
            {
                ["before"] = Side(beforeRows),
                ["after"] = Side(afterRows),
            };
            var beforeMs = compared["before"]!["milliseconds"]!.GetValue<double>();
            var afterMs = compared["after"]!["milliseconds"]!.GetValue<double>();
            compared["speedup"] = beforeMs / afterMs;
            summary[name] = compared;
        }

        return summary;
    }

    private static JsonObject Side(IReadOnlyList<JsonObject> rows)
    {
        var side = new JsonObject();
        foreach (var field in MeasuredFields)
        {
            if (Field(rows[0], field) is null)
                continue;
            side[field] = Statistics.Median(rows.Select(row => Statistics.Median(Numbers(row[field]!))).ToArray());
        }

        var rounds = new JsonArray();
        foreach (var row in rows)
            rounds.Add(Statistics.Median(Numbers(row["milliseconds"]!)));
        side["round_medians_ms"] = rounds;
        return side;
    }

    private static List<string> ResultNames(JsonObject run)
    {
        if (run["results"] is not JsonObject results)
            throw new InvalidOperationException("probe result is missing results");
        return results.Select(property => property.Key).ToList();
    }

    private static List<JsonObject> Rows(IReadOnlyList<JsonObject> runs, string name) =>
        runs.Select(run => ResultRow(run, name)).ToList();

    private static JsonObject ResultRow(JsonObject run, string name)
    {
        if (run["results"] is not JsonObject results || results[name] is not JsonObject row)
            throw new InvalidOperationException($"probe result is missing {name}");
        return row;
    }

    private static JsonNode? Field(JsonObject row, string name) =>
        row.TryGetPropertyValue(name, out var value) ? value : null;

    private static double[] Numbers(JsonNode node)
    {
        if (node is not JsonArray array || array.Count == 0)
            throw new InvalidOperationException("expected a non-empty numeric array");

        var values = new double[array.Count];
        for (var i = 0; i < array.Count; i++)
            values[i] = array[i]!.GetValue<double>();
        return values;
    }
}
