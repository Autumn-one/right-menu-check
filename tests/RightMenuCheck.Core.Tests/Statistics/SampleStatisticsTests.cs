using RightMenuCheck.Core.Statistics;

namespace RightMenuCheck.Core.Tests.Statistics;

public sealed class SampleStatisticsTests
{
    [Fact]
    public void CalculateReturnsNullForNoSamples()
    {
        Assert.Null(SampleStatistics.Calculate([]));
    }

    [Fact]
    public void CalculateUsesAverageMiddleAndNearestRankPercentile()
    {
        var result = SampleStatistics.Calculate([4, 1, 3, 2]);

        Assert.NotNull(result);
        Assert.Equal(4, result.Count);
        Assert.Equal(1, result.Minimum);
        Assert.Equal(2.5, result.Median);
        Assert.Equal(4, result.Percentile95);
        Assert.Equal(4, result.Maximum);
        Assert.Equal(2.5, result.Mean);
    }

    [Fact]
    public void CalculateUsesSingleSampleForEveryStatistic()
    {
        var result = SampleStatistics.Calculate([12.5]);

        Assert.NotNull(result);
        Assert.Equal(12.5, result.Minimum);
        Assert.Equal(12.5, result.Median);
        Assert.Equal(12.5, result.Percentile95);
        Assert.Equal(12.5, result.Maximum);
        Assert.Equal(12.5, result.Mean);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.01)]
    public void CalculateRejectsInvalidDurations(double sample)
    {
        Assert.Throws<ArgumentException>(() => SampleStatistics.Calculate([sample]));
    }
}
