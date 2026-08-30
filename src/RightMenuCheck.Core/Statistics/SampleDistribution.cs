namespace RightMenuCheck.Core.Statistics;

public sealed record SampleDistribution(
    int Count,
    double Minimum,
    double Median,
    double Percentile95,
    double Maximum,
    double Mean);

public static class SampleStatistics
{
    public static SampleDistribution? Calculate(IEnumerable<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var ordered = samples.Order().ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        if (ordered.Any(static sample =>
                double.IsNaN(sample) || double.IsInfinity(sample) || sample < 0))
        {
            throw new ArgumentException(
                "Performance samples must be finite, non-negative numbers.",
                nameof(samples));
        }

        var middle = ordered.Length / 2;
        var median = ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
        var percentile95Index = Math.Max(
            0,
            (int)Math.Ceiling(ordered.Length * 0.95) - 1);

        return new SampleDistribution(
            ordered.Length,
            ordered[0],
            median,
            ordered[percentile95Index],
            ordered[^1],
            ordered.Average());
    }
}
