namespace Campfire.Bench;

public static class MonotonicClock
{
    public static double Seconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

public static class Statistics
{
    /// <summary>
    /// Same index as Ruby <c>(size * fraction).ceil - 1</c> on a sorted sample.
    /// </summary>
    public static double Percentile(IReadOnlyList<double> values, double fraction)
    {
        if (values.Count == 0)
            throw new ArgumentException("Percentile requires at least one sample.", nameof(values));

        var sorted = values.ToArray();
        Array.Sort(sorted);
        var index = (int)Math.Ceiling(sorted.Length * fraction) - 1;
        if (index < 0)
            index = 0;
        return sorted[index];
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            throw new ArgumentException("Median requires at least one sample.", nameof(values));

        var sorted = values.ToArray();
        Array.Sort(sorted);
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
