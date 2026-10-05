namespace Campfire.Bench.Tests;

public class StatisticsTests
{
    [Fact]
    public void Percentile_uses_the_ruby_ceil_index()
    {
        var samples = Enumerable.Range(1, 10).Select(value => (double)value).ToArray();

        Assert.Equal(5, Statistics.Percentile(samples, 0.50));
        Assert.Equal(10, Statistics.Percentile(samples, 0.95));
        Assert.Equal(10, Statistics.Percentile(samples, 0.99));
        Assert.Equal(42, Statistics.Percentile([42], 0.50));
        Assert.Equal(3, Statistics.Percentile([10, 1, 3], 0.50));
    }

    [Fact]
    public void Median_averages_the_two_middle_values_when_the_count_is_even()
    {
        Assert.Equal(2, Statistics.Median([1, 2, 3]));
        Assert.Equal(2.5, Statistics.Median([1, 2, 3, 4]));
        Assert.Equal(1, Statistics.Median([1]));
    }
}
