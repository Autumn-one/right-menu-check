using RightMenuCheck.App.ViewModels;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Core.Statistics;
using RightMenuCheck.Probe.Protocol;
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
    public void RowLabelsUnconfirmedAndFileIdentityOwnersHonestly()
    {
        var unknown = new ContextMenuRowViewModel(CreateMetadata() with
        {
            Owner = CreateUnknownOwner(OwnershipConfidence.None, "Menu registration name"),
        });
        var fileClue = new ContextMenuRowViewModel(CreateMetadata() with
        {
            Owner = CreateUnknownOwner(OwnershipConfidence.Low, "Cloud Music"),
        });

        Assert.Equal("未知应用", unknown.OwnerName);
        Assert.Equal("Cloud Music（文件线索）", fileClue.OwnerName);
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

    [Fact]
    public void SetBenchmarkExplainsGroupedTrialFailures()
    {
        var row = new ContextMenuRowViewModel(CreateMetadata());
        var errorCode = unchecked((int)0x80004005);
        var trials = Enumerable.Range(1, 3)
            .Select(number => new BenchmarkTrialResult(
                number,
                ProbeOutcome.QueryFailed,
                HandlerDurationMilliseconds: null,
                WorkerTotalDurationMilliseconds: 8,
                WorkerProcessId: 100 + number,
                WorkerArchitecture: "X64",
                Phases:
                [
                    new ProbePhaseTiming(
                        ProbePhase.MenuConstruction,
                        DurationMilliseconds: 5,
                        errorCode,
                        Succeeded: false),
                ],
                Menu: null,
                Error: new ProbeError("QueryContextMenu", "Sample was rejected.", errorCode)))
            .ToArray();
        var benchmark = new ContextMenuBenchmarkResult(
            row.Registration.Id,
            IsAggregate: false,
            BenchmarkStatus.Failed,
            AttemptedTrials: 3,
            SuccessfulTrials: 0,
            TimeoutCount: 0,
            CrashCount: 0,
            FailureCount: 3,
            HandlerDuration: null,
            PhaseStatistics: [],
            trials,
            MeasurementScope: "isolated",
            Limitation: null);

        row.SetBenchmark(benchmark);

        Assert.Equal("成功 0/3 · 超时 0 · 崩溃 0 · 其他失败 3", row.TrialSummary);
        Assert.Contains("菜单构建失败（3 次）", row.BenchmarkFailureDetails, StringComparison.Ordinal);
        Assert.Contains("阶段：菜单构建", row.BenchmarkFailureDetails, StringComparison.Ordinal);
        Assert.Contains("HRESULT 0x80004005", row.BenchmarkFailureDetails, StringComparison.Ordinal);
        Assert.Contains("Sample was rejected.", row.BenchmarkFailureDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionalDynamicHandlerExplainsSuccessfulZeroItemProbeAndRiskEvidence()
    {
        const string clsid = "{85212CFD-77ED-4ADD-8E24-A0A39E3DBFC3}";
        var registration = CreateMetadata().Registration with
        {
            Id = "conditional-handler",
            RegistrationPath =
                "Software\\Classes\\*\\shellex\\ContextMenuHandlers\\    sgshellext",
            CanonicalName = "    sgshellext",
            DisplayName = "    sgshellext",
            Kind = ContextMenuRegistrationKind.ClassicContextMenuHandler,
            HandlerClsid = clsid,
        };
        var signature = new AuthenticodeSignatureMetadata(
            SignatureVerificationStatus.Valid,
            PublisherName: "Sogou.com",
            Subject: "CN=Sogou",
            Issuer: null,
            Thumbprint: null,
            ValidFrom: null,
            ValidTo: null,
            TrustErrorCode: null);
        var binary = new BinaryFileMetadata(
            "C:\\Program Files (x86)\\SogouInput\\biz_shellext64.dll",
            Exists: true,
            Size: 1024,
            LastWriteTime: DateTimeOffset.UtcNow,
            Sha256: new string('A', 64),
            Architecture: BinaryArchitectureKind.X64,
            IsManaged: false,
            FileVersion: "1.0.0.0",
            ProductVersion: "1.0.0.0",
            ProductName: "TODO: <产品名>",
            Description: "TODO: <文件说明>",
            CompanyName: "TODO: <公司名>",
            Signature: signature,
            Issues: []);
        var component = new HandlerComponentMetadata(
            HandlerComponentRole.ContextMenuHandler,
            clsid,
            new ComServerRegistration(
                clsid,
                ComServerKind.InProcess,
                "sgshellext class",
                binary.Path,
                binary.Path,
                "Apartment",
                TreatAsClsid: null,
                RegistrySource: null,
                PackageFullName: null,
                PackageServerId: null),
            binary,
            Issues: []);
        var row = new ContextMenuRowViewModel(new ContextMenuRegistrationMetadata(
            registration,
            [component],
            Owner: null,
            Issues: []));
        var emptyMenu = new ProbeMenuSnapshot(
            CommandIdCount: 0,
            Items: [],
            Truncated: false);
        var trials = Enumerable.Range(1, 3)
            .Select(number => new BenchmarkTrialResult(
                number,
                ProbeOutcome.Success,
                HandlerDurationMilliseconds: 2,
                WorkerTotalDurationMilliseconds: 3,
                WorkerProcessId: 200 + number,
                WorkerArchitecture: "X64",
                Phases: [],
                Menu: emptyMenu,
                Error: null))
            .ToArray();
        row.SetBenchmark(new ContextMenuBenchmarkResult(
            registration.Id,
            IsAggregate: false,
            BenchmarkStatus.Completed,
            AttemptedTrials: 3,
            SuccessfulTrials: 3,
            TimeoutCount: 0,
            CrashCount: 0,
            FailureCount: 0,
            HandlerDuration: new SampleDistribution(3, 2, 2, 2, 2, 2),
            PhaseStatistics: [],
            Trials: trials,
            MeasurementScope: "isolated",
            Limitation: null));

        Assert.Equal("sgshellext", row.DisplayName);
        Assert.Contains("原始注册名含空白", row.RegistrationName, StringComparison.Ordinal);
        Assert.Contains("生成 0 项", row.BehaviorSummary, StringComparison.Ordinal);
        Assert.Contains("返回 0 个命令 ID", row.ObservedMenuSummary, StringComparison.Ordinal);
        Assert.Equal("隔离运行时实测", row.EvidenceLevel);
        Assert.Contains("前导或尾随空白", row.RiskSignals, StringComparison.Ordinal);
        Assert.Contains("占位文本", row.RiskSignals, StringComparison.Ordinal);
        Assert.Equal(new string('A', 64), row.Sha256);
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

    private static ApplicationOwnerMetadata CreateUnknownOwner(
        OwnershipConfidence confidence,
        string displayName) => new(
        ApplicationOwnerKind.Unknown,
        confidence,
        displayName,
        Publisher: null,
        Version: null,
        InstallLocation: null,
        ProductCode: null,
        PackageFullName: null,
        UninstallRegistrySource: null,
        UninstallKeyName: null,
        UninstallString: null,
        QuietUninstallString: null,
        IsWindowsInstaller: false,
        IsSystemProtected: false,
        MatchReason: "fixture");
}
