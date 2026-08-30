using RightMenuCheck.App.ViewModels;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Core.Statistics;
using RightMenuCheck.Windows.Benchmark;

namespace RightMenuCheck.IntegrationTests.App;

public sealed class ContextMenuRowViewModelTests
{
    [Fact]
    public void RowFallsBackToCanonicalNameAndUnknownOwner()
    {
        var row = new ContextMenuRowViewModel(CreateMetadata());

        Assert.Equal("canonical-verb", row.DisplayName);
        Assert.Equal("未知应用", row.OwnerName);
        Assert.True(row.IsStaticOnly);
        Assert.Equal("未测试", row.ResultState);
    }

    [Fact]
    public void SetBenchmarkUpdatesVisibleStatisticsAndSortRank()
    {
        var row = new ContextMenuRowViewModel(CreateMetadata());
        var distribution = new SampleDistribution(
            Count: 3,
            Minimum: 2,
            Median: 4.25,
            Percentile95: 7.5,
            Maximum: 7.5,
            Mean: 4.58);
        var benchmark = new ContextMenuBenchmarkResult(
            row.Registration.Id,
            IsAggregate: false,
            BenchmarkStatus.Completed,
            AttemptedTrials: 3,
            SuccessfulTrials: 3,
            TimeoutCount: 0,
            CrashCount: 0,
            FailureCount: 0,
            distribution,
            PhaseStatistics: [],
            Trials: [],
            MeasurementScope: "isolated",
            Limitation: null);

        row.SetBenchmark(benchmark);

        Assert.Equal("4.25 ms", row.Median);
        Assert.Equal("7.50 ms", row.Percentile95);
        Assert.Equal(7.5, row.Percentile95Value);
        Assert.Equal("完成", row.ResultState);
        Assert.Equal(3, row.BenchmarkSortRank);
    }

    private static ContextMenuRegistrationMetadata CreateMetadata()
    {
        var registration = new ContextMenuRegistration
        {
            Id = "test-row",
            Source = new RegistryContextMenuSource(new RegistrySource(
                RegistryHiveKind.LocalMachine,
                RegistryViewKind.Registry64,
                "Software\\Classes\\*\\shell\\canonical-verb")),
            ClassPath = "*",
            RegistrationPath = "Software\\Classes\\*\\shell\\canonical-verb",
            CanonicalName = "canonical-verb",
            DisplayName = string.Empty,
            TargetKind = ContextMenuTargetKind.File,
            Kind = ContextMenuRegistrationKind.StaticVerb,
        };
        return new ContextMenuRegistrationMetadata(
            registration,
            Components: [],
            Owner: null,
            Issues: []);
    }
}
