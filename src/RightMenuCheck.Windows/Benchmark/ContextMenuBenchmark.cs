using RightMenuCheck.Core.Statistics;
using RightMenuCheck.Probe.Protocol;

namespace RightMenuCheck.Windows.Benchmark;

public enum BenchmarkStatus
{
    Completed,
    Partial,
    Failed,
    NotApplicable,
    Unsupported,
}

public sealed record BenchmarkOptions(int TrialCount, TimeSpan Timeout)
{
    public static BenchmarkOptions Default { get; } = new(
        TrialCount: 3,
        Timeout: TimeSpan.FromSeconds(5));
}

public sealed record BenchmarkTarget(ProbeTargetKind Kind, string Path);

public sealed record BenchmarkTrialResult(
    int TrialNumber,
    ProbeOutcome Outcome,
    double? HandlerDurationMilliseconds,
    double WorkerTotalDurationMilliseconds,
    int WorkerProcessId,
    string WorkerArchitecture,
    IReadOnlyList<ProbePhaseTiming> Phases,
    ProbeError? Error);

public sealed record PhaseBenchmarkStatistics(
    ProbePhase Phase,
    SampleDistribution Distribution,
    int SuccessfulSamples,
    int FailedSamples);

public sealed record ContextMenuBenchmarkResult(
    string Id,
    bool IsAggregate,
    BenchmarkStatus Status,
    int AttemptedTrials,
    int SuccessfulTrials,
    int TimeoutCount,
    int CrashCount,
    int FailureCount,
    SampleDistribution? HandlerDuration,
    IReadOnlyList<PhaseBenchmarkStatistics> PhaseStatistics,
    IReadOnlyList<BenchmarkTrialResult> Trials,
    string MeasurementScope,
    string? Limitation)
{
    public double? FailureRate => AttemptedTrials == 0
        ? null
        : (AttemptedTrials - SuccessfulTrials) / (double)AttemptedTrials;
}

public sealed record BenchmarkFailureReason(
    ProbeOutcome Outcome,
    ProbePhase? FailedPhase,
    string? ErrorType,
    string? ErrorMessage,
    int? HResult,
    int Count);

public static class BenchmarkFailureAnalyzer
{
    public static IReadOnlyList<BenchmarkFailureReason> Group(
        ContextMenuBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Trials
            .Where(static trial => trial.Outcome != ProbeOutcome.Success)
            .Select(static trial =>
            {
                var failedPhase = trial.Phases.LastOrDefault(static phase => !phase.Succeeded);
                return new FailureKey(
                    trial.Outcome,
                    failedPhase?.Phase,
                    trial.Error?.Type,
                    trial.Error?.Message,
                    trial.Error?.HResult ?? failedPhase?.HResult);
            })
            .GroupBy(static failure => failure)
            .Select(static group => new BenchmarkFailureReason(
                group.Key.Outcome,
                group.Key.FailedPhase,
                group.Key.ErrorType,
                group.Key.ErrorMessage,
                group.Key.HResult,
                group.Count()))
            .OrderByDescending(static reason => reason.Count)
            .ThenBy(static reason => reason.Outcome)
            .ThenBy(static reason => reason.FailedPhase)
            .ThenBy(static reason => reason.ErrorType, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record FailureKey(
        ProbeOutcome Outcome,
        ProbePhase? FailedPhase,
        string? ErrorType,
        string? ErrorMessage,
        int? HResult);
}

public sealed class ContextMenuBenchmarkComparer : IComparer<ContextMenuBenchmarkResult>
{
    public static ContextMenuBenchmarkComparer Instance { get; } = new();

    public int Compare(ContextMenuBenchmarkResult? left, ContextMenuBenchmarkResult? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var result = GetSeverity(left).CompareTo(GetSeverity(right));
        if (result != 0)
        {
            return result;
        }

        result = right.TimeoutCount.CompareTo(left.TimeoutCount);
        if (result != 0)
        {
            return result;
        }

        result = right.CrashCount.CompareTo(left.CrashCount);
        if (result != 0)
        {
            return result;
        }

        result = Nullable.Compare(
            right.HandlerDuration?.Percentile95,
            left.HandlerDuration?.Percentile95);
        return result != 0
            ? result
            : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
    }

    private static int GetSeverity(ContextMenuBenchmarkResult result)
    {
        if (result.TimeoutCount > 0)
        {
            return 0;
        }

        if (result.CrashCount > 0)
        {
            return 1;
        }

        if (result.Status == BenchmarkStatus.Failed || result.FailureCount > 0)
        {
            return 2;
        }

        if (result.HandlerDuration is not null)
        {
            return 3;
        }

        return 4;
    }
}
