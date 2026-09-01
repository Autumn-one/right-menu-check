using System.IO;
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

    public string DisplayName => (string.IsNullOrWhiteSpace(Registration.DisplayName)
        ? Registration.CanonicalName
        : Registration.DisplayName).Trim();

    public string RegistrationName => Registration.CanonicalName.Equals(
        Registration.CanonicalName.Trim(),
        StringComparison.Ordinal)
        ? Registration.CanonicalName
        : $"{Registration.CanonicalName.Trim()}（原始注册名含空白）";

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
                                       .FirstOrDefault(IsMeaningfulFileDescription) ??
                                   Metadata.Components
                                       .Select(static component => component.Binary?.Description)
                                       .FirstOrDefault(IsMeaningfulFileDescription) ??
                                   (Metadata.Components.Any(static component =>
                                       IsPlaceholderFileDescription(component.Binary?.ProductName) ||
                                       IsPlaceholderFileDescription(component.Binary?.Description))
                                       ? "未提供（文件资源为占位文本）"
                                       : "未提供");

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

    public string BehaviorSummary => GetBehaviorSummary();

    public string EvidenceLevel => GetEvidenceLevel();

    public string ObservedMenuSummary => GetObservedMenuSummary();

    public string ObservedMenuItems => FormatObservedMenuItems();

    public string RiskSignals => FormatRiskSignals();

    public string RegistrationPath => Registration.RegistrationPath;

    public string BinaryPath => Metadata.Components
                                    .Select(static component =>
                                        component.Binary?.Path ??
                                        component.ComServer?.ResolvedServerPath)
                                    .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
                                "未解析";

    public string ComClassName => Metadata.Components
                                      .Select(static component => component.ComServer?.DisplayName)
                                      .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
                                  "未提供";

    public string FileVersion => Metadata.Components
                                     .Select(static component => component.Binary?.FileVersion)
                                     .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
                                 "未知";

    public string Sha256 => Metadata.Components
                                .Select(static component => component.Binary?.Sha256)
                                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
                            "未获得";

    public string SignaturePublisher => Metadata.Components
                                            .Select(static component =>
                                                component.Binary?.Signature.Subject ??
                                                component.Binary?.Signature.PublisherName)
                                            .FirstOrDefault(static value =>
                                                !string.IsNullOrWhiteSpace(value)) ??
                                        "未获得";

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
        BinaryPath,
        BehaviorSummary,
        ObservedMenuItems,
        RiskSignals,
        Sha256);

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
        BenchmarkFailureDetails,
        RegistrationName,
        BehaviorSummary,
        EvidenceLevel,
        ObservedMenuSummary,
        ObservedMenuItems,
        RiskSignals,
        ComClassName,
        FileVersion,
        Sha256,
        SignaturePublisher);

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
        OnPropertyChanged(nameof(BehaviorSummary));
        OnPropertyChanged(nameof(EvidenceLevel));
        OnPropertyChanged(nameof(ObservedMenuSummary));
        OnPropertyChanged(nameof(ObservedMenuItems));
        OnPropertyChanged(nameof(RiskSignals));
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

    private string GetBehaviorSummary()
    {
        var menu = GetRepresentativeMenu();
        return Registration.Kind switch
        {
            ContextMenuRegistrationKind.ClassicContextMenuHandler when menu is { Items.Count: 0 } =>
                "动态 COM 扩展已加载，但当前样本生成 0 项；功能受文件类型、配置或运行时策略控制。",
            ContextMenuRegistrationKind.ClassicContextMenuHandler when menu is not null =>
                $"动态 COM 扩展为当前样本生成 {menu.Items.Count} 个可检查条目。",
            ContextMenuRegistrationKind.ClassicContextMenuHandler =>
                "动态 COM 扩展；菜单内容由 DLL 根据所选对象在运行时生成。",
            ContextMenuRegistrationKind.ExplorerCommand or
                ContextMenuRegistrationKind.PackagedExplorerCommand when menu is { Items.Count: 0 } =>
                "Explorer 命令已加载，但当前样本没有可见命令。",
            ContextMenuRegistrationKind.ExplorerCommand or
                ContextMenuRegistrationKind.PackagedExplorerCommand when menu is not null =>
                $"Explorer 命令为当前样本返回 {menu.Items.Count} 个可检查条目。",
            ContextMenuRegistrationKind.ExplorerCommand or
                ContextMenuRegistrationKind.PackagedExplorerCommand =>
                "动态 Explorer 命令；标题和子命令需要通过隔离探查获取。",
            ContextMenuRegistrationKind.StaticVerb => Registration.Command is null
                ? "注册表静态菜单，但没有读取到执行命令。"
                : "注册表直接定义执行命令；只有用户选择该菜单时才会启动。",
            ContextMenuRegistrationKind.DelegateExecuteVerb =>
                "选择后交给 DelegateExecute COM 组件执行；安全探查不会调用它。",
            ContextMenuRegistrationKind.CascadingVerb => Registration.SubCommands.Count == 0
                ? "注册表级联菜单；子项记录在嵌套注册键中。"
                : $"注册表级联菜单，声明 {Registration.SubCommands.Count} 个子命令。",
            _ => "未识别的菜单执行模型。",
        };
    }

    private string GetEvidenceLevel()
    {
        if (_operationError is not null)
        {
            return "探查失败";
        }

        if (GetRepresentativeMenu() is not null)
        {
            return "隔离运行时实测";
        }

        return Registration.Kind is ContextMenuRegistrationKind.StaticVerb or
            ContextMenuRegistrationKind.CascadingVerb
            ? "注册表直接证据"
            : "注册及文件身份证据";
    }

    private string GetObservedMenuSummary()
    {
        if (_operationError is not null)
        {
            return $"未获得运行时菜单：{_operationError}";
        }

        if (Registration.Kind == ContextMenuRegistrationKind.StaticVerb)
        {
            return Registration.Command is null
                ? "未读取到静态命令。"
                : "静态命令来自注册表，无需加载第三方 DLL。";
        }

        if (Registration.Kind == ContextMenuRegistrationKind.DelegateExecuteVerb)
        {
            return "为避免执行实际功能，不调用 DelegateExecute。";
        }

        if (Registration.Kind == ContextMenuRegistrationKind.CascadingVerb)
        {
            return "级联结构来自注册表；嵌套子项会作为独立记录显示。";
        }

        var menu = GetRepresentativeMenu();
        if (menu is null)
        {
            return _benchmark is null
                ? "尚未隔离探查。"
                : $"探查结束但未获得菜单快照：{ResultState}";
        }

        var trialCount = _benchmark!.Trials.Count(static trial =>
            trial.Outcome == ProbeOutcome.Success && trial.Menu is not null);
        if (menu.Items.Count == 0)
        {
            return $"隔离探查成功 {trialCount} 次：处理器返回 0 个命令 ID，当前样本不会显示菜单。";
        }

        var truncation = menu.Truncated
            ? $"；快照不完整：{menu.Limitation ?? "部分内容无法读取"}"
            : string.Empty;
        return $"隔离探查成功 {trialCount} 次：捕获 {menu.Items.Count} 个条目，" +
               $"处理器占用 {menu.CommandIdCount} 个命令 ID{truncation}。";
    }

    private string FormatObservedMenuItems()
    {
        if (Registration.Kind == ContextMenuRegistrationKind.StaticVerb)
        {
            return Registration.Command ?? "未读取到命令";
        }

        if (Registration.Kind == ContextMenuRegistrationKind.CascadingVerb &&
            Registration.SubCommands.Count > 0)
        {
            return string.Join(Environment.NewLine, Registration.SubCommands.Select(static item =>
                $"- {item}"));
        }

        var menu = GetRepresentativeMenu();
        if (menu is null)
        {
            return _operationError ?? "尚未探查";
        }

        if (menu.Items.Count == 0)
        {
            return "（当前样本未生成菜单项）";
        }

        var lines = menu.Items.Select(item =>
        {
            var indent = new string(' ', Math.Min(item.Depth, 8) * 2);
            var title = item.Kind switch
            {
                ProbeMenuItemKind.Separator => "────────",
                ProbeMenuItemKind.OwnerDrawn => "[自绘菜单项，标题不可读取]",
                _ => item.Title ?? "[标题不可读取]",
            };
            var evidence = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.CanonicalVerb))
            {
                evidence.Add($"动词 {SanitizeProbeText(item.CanonicalVerb)}");
            }

            if (!string.IsNullOrWhiteSpace(item.HelpText))
            {
                evidence.Add($"说明 {SanitizeProbeText(item.HelpText)}");
            }

            if (item.IsDisabled)
            {
                evidence.Add("禁用");
            }

            if (item.IsHidden)
            {
                evidence.Add("隐藏");
            }

            var suffix = evidence.Count == 0 ? string.Empty : $" · {string.Join(" · ", evidence)}";
            return $"{indent}- {title}{suffix}";
        }).ToList();
        if (menu.Truncated)
        {
            lines.Add($"- [快照不完整：{menu.Limitation ?? "部分内容无法读取"}]");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string FormatRiskSignals()
    {
        var signals = new List<string>();
        if (Registration.ClassPath is "*" or "AllFilesystemObjects" ||
            Registration.TargetKind is ContextMenuTargetKind.Drive or
                ContextMenuTargetKind.DesktopBackground)
        {
            signals.Add($"覆盖范围广：{Scope}");
        }

        if (!Registration.CanonicalName.Equals(
                Registration.CanonicalName.Trim(),
                StringComparison.Ordinal))
        {
            signals.Add("注册键名称含前导或尾随空白，降低可识别性");
        }

        var menu = GetRepresentativeMenu();
        if (IsDynamicHandler() && menu is { Items.Count: 0 })
        {
            signals.Add("动态处理器成功加载，但当前样本生成 0 项");
        }

        var observedSignatures = _benchmark?.Trials
            .Where(static trial => trial.Outcome == ProbeOutcome.Success && trial.Menu is not null)
            .Select(static trial => string.Join('\u001F', trial.Menu!.Items.Select(static item =>
                $"{item.Depth}:{item.Kind}:{item.Title}:{item.CanonicalVerb}")))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() ?? 0;
        if (observedSignatures > 1)
        {
            signals.Add("不同隔离试次返回的菜单内容不一致");
        }

        var binaries = Metadata.Components
            .Select(static component => component.Binary)
            .Where(static binary => binary is not null)
            .Select(static binary => binary!)
            .ToArray();
        if (Metadata.Components.Count > 0 && binaries.Length == 0)
        {
            signals.Add("COM 组件未解析到可检查的二进制文件");
        }

        if (binaries.Any(static binary => !binary.Exists))
        {
            signals.Add("注册指向的二进制文件不存在");
        }

        if (binaries.Any(static binary =>
                IsPlaceholderFileDescription(binary.ProductName) ||
                IsPlaceholderFileDescription(binary.Description) ||
                IsPlaceholderFileDescription(binary.CompanyName)))
        {
            signals.Add("二进制版本资源使用占位文本，无法说明真实用途");
        }

        if (binaries.Any(static binary => binary.Signature.Status is
                SignatureVerificationStatus.NoSignature or
                SignatureVerificationStatus.Invalid or
                SignatureVerificationStatus.Error))
        {
            signals.Add("二进制缺少有效 Authenticode 签名");
        }

        if (binaries.Any(static binary => IsUserWritableLocation(binary.Path)))
        {
            signals.Add("处理器二进制位于当前用户可写目录");
        }

        if (Metadata.Owner is null or
            { Kind: ApplicationOwnerKind.Unknown } or
            { Confidence: OwnershipConfidence.None or OwnershipConfidence.Low })
        {
            signals.Add("所属应用无法高可信确认");
        }

        if (_operationError is not null)
        {
            signals.Add("隔离探查未成功完成");
        }

        return signals.Count == 0
            ? "未命中内置高风险线索；这不等同于安全结论。"
            : string.Join(Environment.NewLine, signals.Select(static signal => $"- {signal}"));
    }

    private ProbeMenuSnapshot? GetRepresentativeMenu() => _benchmark?.Trials
        .Where(static trial => trial.Outcome == ProbeOutcome.Success && trial.Menu is not null)
        .Select(static trial => trial.Menu!)
        .OrderByDescending(static menu => menu.Items.Count)
        .ThenByDescending(static menu => menu.CommandIdCount)
        .FirstOrDefault();

    private bool IsDynamicHandler() => Registration.Kind is
        ContextMenuRegistrationKind.ClassicContextMenuHandler or
        ContextMenuRegistrationKind.ExplorerCommand or
        ContextMenuRegistrationKind.PackagedExplorerCommand;

    private static bool IsMeaningfulFileDescription(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !IsPlaceholderFileDescription(value);

    private static bool IsPlaceholderFileDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        return text.StartsWith("TODO", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<产品名>", StringComparison.Ordinal) ||
               text.Contains("<文件说明>", StringComparison.Ordinal) ||
               text.Contains("<公司名>", StringComparison.Ordinal);
    }

    private static bool IsUserWritableLocation(string path)
    {
        try
        {
            var normalized = Path.GetFullPath(path);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return !string.IsNullOrWhiteSpace(userProfile) && normalized.StartsWith(
                $"{Path.GetFullPath(userProfile).TrimEnd(Path.DirectorySeparatorChar)}" +
                Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
                                           ArgumentException or
                                           NotSupportedException or
                                           PathTooLongException)
        {
            return false;
        }
    }

    private static string SanitizeProbeText(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

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
    string FailureDetails,
    string RegistrationName,
    string BehaviorSummary,
    string EvidenceLevel,
    string ObservedMenuSummary,
    string ObservedMenuItems,
    string RiskSignals,
    string ComClassName,
    string FileVersion,
    string Sha256,
    string SignaturePublisher);
