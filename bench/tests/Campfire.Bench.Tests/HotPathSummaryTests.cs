using System.Text.Json.Nodes;

namespace Campfire.Bench.Tests;

public class HotPathSummaryTests
{
    [Fact]
    public void Summarizes_median_of_round_medians_and_speedup()
    {
        var before = new[]
        {
            Probe(milliseconds: [1, 3], allocations: [10, 30], queries: [4, 4], hash: "abc", contentTypeFirst: true),
            Probe(milliseconds: [4, 6], allocations: [40, 60], queries: [8, 8], hash: "abc", contentTypeFirst: false),
        };
        var after = new[]
        {
            Probe(milliseconds: [1, 1], allocations: [2, 2], queries: [1, 1], hash: "abc", contentTypeFirst: true),
            Probe(milliseconds: [1, 3], allocations: [4, 4], queries: [1, 3], hash: "abc", contentTypeFirst: false),
        };

        var summary = HotPathSummary.Results(before, after);
        var room = summary["room_cold"]!.AsObject();

        Assert.Equal(3.5, room["before"]!["milliseconds"]!.GetValue<double>());
        Assert.Equal(35, room["before"]!["allocations"]!.GetValue<double>());
        Assert.Equal(6, room["before"]!["queries"]!.GetValue<double>());
        Assert.Equal(1.5, room["after"]!["milliseconds"]!.GetValue<double>());
        Assert.Equal(3.5 / 1.5, room["speedup"]!.GetValue<double>());
        Assert.Equal(2, room["before"]!["round_medians_ms"]![0]!.GetValue<double>());
        Assert.Equal(5, room["before"]!["round_medians_ms"]![1]!.GetValue<double>());
        Assert.Null(room["before"]!["payload_sha256"]);
    }

    [Fact]
    public void Fanout_rows_omit_query_counts()
    {
        var before = new[] { Fanout([1, 3]), Fanout([3, 5]) };
        var after = new[] { Fanout([1, 1]), Fanout([1, 1]) };

        var summary = HotPathSummary.Results(before, after);
        var fanout = summary["unread_fanout_1000"]!["before"]!.AsObject();

        Assert.Equal(3, fanout["milliseconds"]!.GetValue<double>());
        Assert.False(fanout.ContainsKey("queries"));
    }

    [Fact]
    public void Differing_bodies_fail_before_a_speedup_is_reported()
    {
        var before = new[] { Probe([1], [1], [1], "abc", true) };
        var after = new[] { Probe([1], [1], [1], "def", true) };

        var failure = Assert.Throws<InvalidOperationException>(() => HotPathSummary.Results(before, after));

        Assert.Equal("room_cold: body_sha256 differs; no winning summary written", failure.Message);
    }

    private static JsonObject Probe(double[] milliseconds, double[] allocations, double[] queries, string hash, bool contentTypeFirst)
    {
        var headers = contentTypeFirst
            ? new JsonObject { ["content-type"] = "text/html", ["etag"] = "v" }
            : new JsonObject { ["etag"] = "v", ["content-type"] = "text/html" };
        return new JsonObject
        {
            ["results"] = new JsonObject
            {
                ["room_cold"] = new JsonObject
                {
                    ["milliseconds"] = Numbers(milliseconds),
                    ["allocations"] = Numbers(allocations),
                    ["queries"] = Numbers(queries),
                    ["body_sha256"] = new JsonArray(JsonValue.Create(hash)),
                    ["headers"] = new JsonArray(headers),
                },
            },
        };
    }

    private static JsonObject Fanout(double[] milliseconds) => new()
    {
        ["results"] = new JsonObject
        {
            ["unread_fanout_1000"] = new JsonObject
            {
                ["milliseconds"] = Numbers(milliseconds),
                ["allocations"] = Numbers(milliseconds),
                ["payload_sha256"] = "same",
            },
        },
    };

    private static JsonArray Numbers(params double[] values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }
}
