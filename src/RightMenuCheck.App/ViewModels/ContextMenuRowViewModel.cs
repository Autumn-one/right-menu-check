using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Benchmark;

namespace RightMenuCheck.App.ViewModels;

public sealed class ContextMenuRowViewModel : ObservableObject
{
    private ContextMenuBenchmarkResult? _benchmark;
    private string? _operationError;

    public ContextMenuRowViewModel(ContextMenuRegistrationMetadata metadata)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    public ContextMenuRegistrationMetadata Metadata { get; }

    public ContextMenuRegistration Registration => Metadata.Registration;

    public string DisplayName => string.IsNullOrWhiteSpace(Registration.DisplayName)
        ? Registration.CanonicalName
        : Registration.DisplayName;

    public string OwnerName => string.IsNullOrWhiteSpace(Metadata.Owner?.DisplayName)
        ? "未知应用"
        : Metadata.Owner switch
        {
            { Kind: ApplicationOwnerKind.Unknown, Confidence: OwnershipConfidence.None } => "未知应用",
            { Kind: ApplicationOwnerKind.Unknown } owner => $"{owner.DisplayName}（文件线索）",
            { Confidence: OwnershipConfidence.Low } owner => $"{owner.DisplayName}（低可信）",
            { } owner => owner.DisplayName,
            _ => "未知应用",
        };

    public string OwnerEvidence => Metadata.Owner is not { } owner
        ? "未获得归属信息"
        : $"{GetConfidenceText(owner.Confidence)} · {owner.MatchReason}";

    public string BinaryProduct => Metadata.Components
                                       .Select(static component => component.Binary?.ProductName)
                                       .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
                                   Metadata.Components
                                       .Select(static component => component.Binary?.Description)
                                       .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
                                   "未提供";

    public string Publisher => Metadata.Owner?.Publisher ??
                               Metadata.Components
                                   .Select(static component =>
                                       component.Binary?.Signature.PublisherName ??
                                       component.Binary?.CompanyName)
                                   .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
                               "未知发布者";

    public string RegistrationType => Registration.Kind switch
    {
        ContextMenuRegistrationKind.ClassicContextMenuHandler => "经典扩展",
        ContextMenuRegistrationKind.StaticVerb => "静态命令",
        ContextMenuRegistrationKind.DelegateExecuteVerb => "委托命令",
        ContextMenuRegistrationKind.ExplorerCommand => "Explorer 命令",
        ContextMenuRegistrationKind.CascadingVerb => "级联菜单",
        ContextMenuRegistrationKind.PackagedExplorerCommand => "现代菜单",
        _ => "未知类型",
    };

    public string Scope => Registration.TargetKind switch
    {
        ContextMenuTargetKind.File => "文件",
        ContextMenuTargetKind.FileType => GetFileTypeScope(),
        ContextMenuTargetKind.AllFileSystemObjects => "所有文件系统对象",
        ContextMenuTargetKind.Folder => "文件夹",
        ContextMenuTargetKind.FolderBackground => "文件夹空白处",
        ContextMenuTargetKind.Drive => "驱动器",
        ContextMenuTargetKind.DesktopBackground => "桌面空白处",
        ContextMenuTargetKind.LibraryFolder => "库文件夹",
        ContextMenuTargetKind.LibraryBackground => "库空白处",
        _ => "未知范围",
    };

    public string ScopeAndType => $"{Scope} · {RegistrationType}";

    public string State => GetStateText();

    public bool IsEnabled => Registration.IsVisibleByDefault;

    public bool IsModern => Registration.Source is PackageContextMenuSource;

    public bool IsStaticOnly => Registration.Kind is
        ContextMenuRegistrationKind.StaticVerb or
        ContextMenuRegistrationKind.DelegateExecuteVerb;

    public bool IsThirdParty => !Publisher.Contains(
        "Microsoft",
        StringComparison.OrdinalIgnoreCase);

    public string Architecture => GetArchitectureText();

    public string Source => Registration.Source switch
    {
        RegistryContextMenuSource registry =>
            $"{(registry.Location.Hive == RegistryHiveKind.CurrentUser ? "HKCU" : "HKLM")} · " +
            $"{(registry.Location.View == RegistryViewKind.Registry64 ? "64 位" : "32 位")}",
        PackageContextMenuSource package => $"MSIX · {package.PackageName}",
        _ => "未知来源",
    };

    public string HandlerClsid => Registration.HandlerClsid ??
                                  Registration.CommandStateHandlerClsid ??
                                  Registration.DelegateExecuteClsid ??
                                  "不适用";

    public string Command => Registration.Command ?? "不适用";

    public string RegistrationPath => Registration.RegistrationPath;

    public string BinaryPath => Metadata.Components
                                    .Select(static component =>
                                        component.Binary?.Path ??
                                        component.ComServer?.ResolvedServerPath)
                                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
                                "未解析";

    public string Signature => Metadata.Components
                                   .Select(static component => component.Binary?.Signature.Status)
                                   .FirstOrDefault(static value => value is not null) switch
    {
        SignatureVerificationStatus.Valid => "有效签名",
        SignatureVerificationStatus.NoSignature => "无签名",
        SignatureVerificationStatus.Invalid => "签名无效",
        SignatureVerificationStatus.Error => "验证错误",
        _ => "未知",
    };

    public ContextMenuBenchmarkResult? Benchmark => _benchmark;

    public string Median => FormatMilliseconds(_benchmark?.HandlerDuration?.Median);

    public string Percentile95 => FormatMilliseconds(_benchmark?.HandlerDuration?.Percentile95);

    public double? Percentile95Value => _benchmark?.HandlerDuration?.Percentile95;

    public int TimeoutCount => _benchmark?.TimeoutCount ?? 0;

    public int FailureCount => _benchmark is null
        ? 0
        : _benchmark.CrashCount + _benchmark.FailureCount;

    public string ResultState => _operationError ?? _benchmark?.Status switch
    {
        BenchmarkStatus.Completed => "完成",
        BenchmarkStatus.Partial => "部分失败",
        BenchmarkStatus.Failed => "失败",
        BenchmarkStatus.NotApplicable => "不可归因",
        BenchmarkStatus.Unsupported => "不支持",
        _ => "未测试",
    };

    public int BenchmarkSortRank => _benchmark switch
    {
        { TimeoutCount: > 0 } => 0,
        { CrashCount: > 0 } => 1,
        { FailureCount: > 0 } => 2,
        { HandlerDuration: not null } => 3,
        _ => 4,
    };

    public string MeasurementScope => _benchmark?.MeasurementScope ?? "未测试";

    public string Limitation => _operationError ?? _benchmark?.Limitation ?? "无";

    public string TrialSummary => _benchmark is null
        ? "未测试"
        : $"成功 {_benchmark.SuccessfulTrials}/{_benchmark.AttemptedTrials} · " +
          $"超时 {_benchmark.TimeoutCount} · 崩溃 {_benchmark.CrashCount} · " +
          $"其他失败 {_benchmark.FailureCount}";

    public string BenchmarkFailureDetails => _operationError ?? FormatFailureDetails(_benchmark);

    public string SearchText => string.Join(
        ' ',
        DisplayName,
        OwnerName,
        Publisher,
        RegistrationType,
        Scope,
        HandlerClsid,
        RegistrationPath,
        BinaryPath);

    public void SetBenchmark(ContextMenuBenchmarkResult benchmark)
    {
        _benchmark = benchmark ?? throw new ArgumentNullException(nameof(benchmark));
        _operationError = null;
        RaiseBenchmarkProperties();
    }

    public void SetOperationError(string error)
    {
        _operationError = error;
        RaiseBenchmarkProperties();
    }

    public ContextMenuExportRow ToExport() => new(
        DisplayName,
        OwnerName,
        Publisher,
        RegistrationType,
        Scope,
        State,
        Architecture,
        Source,
        HandlerClsid,
        RegistrationPath,
        BinaryPath,
        Signature,
        _benchmark?.Status.ToString() ?? "NotRun",
        _benchmark?.HandlerDuration?.Median,
        _benchmark?.HandlerDuration?.Percentile95,
        TimeoutCount,
        FailureCount,
        _benchmark?.FailureRate,
        Limitation,
        OwnerEvidence,
        BinaryProduct,
        TrialSummary,
        BenchmarkFailureDetails);

    private void RaiseBenchmarkProperties()
    {
        OnPropertyChanged(nameof(Benchmark));
        OnPropertyChanged(nameof(Median));
        OnPropertyChanged(nameof(Percentile95));
        OnPropertyChanged(nameof(Percentile95Value));
        OnPropertyChanged(nameof(TimeoutCount));
        OnPropertyChanged(nameof(FailureCount));
        OnPropertyChanged(nameof(ResultState));
        OnPropertyChanged(nameof(BenchmarkSortRank));
        OnPropertyChanged(nameof(MeasurementScope));
        OnPropertyChanged(nameof(Limitation));
        OnPropertyChanged(nameof(TrialSummary));
        OnPropertyChanged(nameof(BenchmarkFailureDetails));
    }

    private string GetFileTypeScope()
    {
        var classPath = Registration.ClassPath;
        if (classPath.StartsWith('.'))
        {
            return $"文件 {classPath}";
        }

        var extensions = Registration.FileAssociations
            .Select(static association => association.Extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        return extensions.Length == 0
            ? "特定文件类型"
            : $"文件 {string.Join(", ", extensions)}";
    }

    private string GetStateText()
    {
        var status = Registration.Status;
        if (status.HasFlag(ContextMenuRegistrationStatus.Blocked))
        {
            return "已阻止";
        }

        if (status.HasFlag(ContextMenuRegistrationStatus.LegacyDisabled) ||
            status.HasFlag(ContextMenuRegistrationStatus.ProgrammaticOnly))
        {
            return "已隐藏";
        }

        if (status.HasFlag(ContextMenuRegistrationStatus.ExtendedOnly))
        {
            return "Shift 时显示";
        }

        if (status.HasFlag(ContextMenuRegistrationStatus.CurrentUserOverridePresent))
        {
            return "存在用户覆盖";
        }

        return "已启用";
    }

    private string GetArchitectureText()
    {
        var binaryArchitecture = Metadata.Components
            .Select(static component => component.Binary?.Architecture)
            .FirstOrDefault(static value => value is not null);
        if (binaryArchitecture is not null)
        {
            return binaryArchitecture.Value switch
            {
                BinaryArchitectureKind.X86 => "x86",
                BinaryArchitectureKind.X64 => "x64",
                BinaryArchitectureKind.Arm => "ARM",
                BinaryArchitectureKind.Arm64 => "ARM64",
                BinaryArchitectureKind.AnyCpu => "AnyCPU",
                BinaryArchitectureKind.AnyCpuPrefer32Bit => "AnyCPU/x86",
                _ => "未知",
            };
        }

        return Registration.Source switch
        {
            RegistryContextMenuSource registry =>
                registry.Location.View == RegistryViewKind.Registry64 ? "x64 视图" : "x86 视图",
            PackageContextMenuSource package => package.Architecture.ToString(),
            _ => "未知",
        };
    }

    private static string FormatMilliseconds(double? value) =>
        value is null ? "—" : $"{value.Value:F2} ms";

    private static string FormatFailureDetails(ContextMenuBenchmarkResult? benchmark)
    {
        if (benchmark is null)
        {
            return "未测试";
        }

        var reasons = BenchmarkFailureAnalyzer.Group(benchmark);
        return reasons.Count == 0
            ? "无"
            : string.Join(Environment.NewLine, reasons.Select(FormatFailureReason));
    }

    private static string FormatFailureReason(BenchmarkFailureReason reason)
    {
        var parts = new List<string>
        {
            $"{GetOutcomeText(reason.Outcome)}（{reason.Count} 次）",
        };
        if (reason.FailedPhase is { } phase)
        {
            parts.Add($"阶段：{GetPhaseText(phase)}");
        }

        if (!string.IsNullOrWhiteSpace(reason.ErrorType))
        {
            parts.Add($"类型：{reason.ErrorType}");
        }

        if (reason.HResult is { } hResult)
        {
            parts.Add($"HRESULT 0x{unchecked((uint)hResult):X8}");
        }

        if (!string.IsNullOrWhiteSpace(reason.ErrorMessage))
        {
            var message = reason.ErrorMessage.Replace('\r', ' ').Replace('\n', ' ').Trim();
            parts.Add(message.Length <= 280 ? message : $"{message[..280]}…");
        }

        return string.Join(" · ", parts);
    }

    private static string GetConfidenceText(OwnershipConfidence confidence) => confidence switch
    {
        OwnershipConfidence.Exact => "精确匹配",
        OwnershipConfidence.High => "高可信",
        OwnershipConfidence.Medium => "中可信",
        OwnershipConfidence.Low => "仅作线索",
        _ => "未确认",
    };

    private static string GetOutcomeText(ProbeOutcome outcome) => outcome switch
    {
        ProbeOutcome.NotApplicable => "样本不适用",
        ProbeOutcome.InvalidRequest => "测试请求无效",
        ProbeOutcome.ActivationFailed => "COM 激活失败",
        ProbeOutcome.InitializationFailed => "Shell 初始化失败",
        ProbeOutcome.QueryFailed => "菜单构建失败",
        ProbeOutcome.TimedOut => "测试超时",
        ProbeOutcome.Crashed => "扩展进程崩溃",
        ProbeOutcome.ProtocolError => "测试通信错误",
        _ => outcome.ToString(),
    };

    private static string GetPhaseText(ProbePhase phase) => phase switch
    {
        ProbePhase.ComActivation => "COM 激活",
        ProbePhase.ShellInitialization => "Shell 初始化",
        ProbePhase.MenuConstruction => "菜单构建",
        ProbePhase.GetTitle => "读取标题",
        ProbePhase.GetIcon => "读取图标",
        ProbePhase.GetState => "读取状态",
        ProbePhase.EnumerateSubCommands => "枚举子命令",
        ProbePhase.AggregateMenuCreation => "整体菜单构建",
        _ => phase.ToString(),
    };
}

public sealed record ContextMenuExportRow(
    string Name,
    string Owner,
    string Publisher,
    string Type,
    string Scope,
    string State,
    string Architecture,
    string Source,
    string HandlerClsid,
    string RegistrationPath,
    string BinaryPath,
    string Signature,
    string BenchmarkStatus,
    double? MedianMilliseconds,
    double? Percentile95Milliseconds,
    int TimeoutCount,
    int FailureCount,
    double? FailureRate,
    string Limitation,
    string OwnerEvidence,
    string BinaryProduct,
    string TrialSummary,
    string FailureDetails);
