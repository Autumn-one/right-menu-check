using System.ComponentModel;
using System.Diagnostics;
using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Diagnostics;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Windows.Management;

public enum WindowsContextMenuMode
{
    Windows11,
    Classic,
    Custom,
    Unsupported,
}

public sealed record WindowsContextMenuModeStatus(
    WindowsContextMenuMode Mode,
    bool CanChange,
    string Detail,
    int WindowsBuild,
    RegistryViewKind NativeRegistryView);

public sealed record WindowsContextMenuModePlan(
    WindowsContextMenuMode TargetMode,
    WindowsContextMenuModeStatus Current,
    bool IsSupported,
    bool IsNoChange,
    string? BlockReason,
    string ImpactDescription,
    RegistryMutationPlan? MutationPlan);

public sealed record WindowsContextMenuModeChangeResult(
    bool Succeeded,
    bool IsNoChange,
    string Message,
    WindowsContextMenuModeStatus Status,
    string? JournalPath);

public sealed record ExplorerRestartResult(
    bool Succeeded,
    int ClosedProcessCount,
    int? NewProcessId,
    string Message);

public interface IWindowsBuildProvider
{
    int Build { get; }
}

public sealed class SystemWindowsBuildProvider : IWindowsBuildProvider
{
    public int Build => Environment.OSVersion.Version.Build;
}

public sealed class WindowsContextMenuModePlanner
{
    public const string OverrideClsid = "{86CA1AA0-34AA-4E8B-A509-50C905BAE2A2}";
    public const string OverrideClsidKeyPath =
        $"Software\\Classes\\CLSID\\{OverrideClsid}";
    public const string OverrideKeyPath =
        $"{OverrideClsidKeyPath}\\InprocServer32";
    private const int MinimumWindows11Build = 22000;
    private readonly IRegistryReader _registryReader;
    private readonly IWindowsBuildProvider _buildProvider;
    private readonly RegistryViewKind _nativeRegistryView;

    public WindowsContextMenuModePlanner(
        IRegistryReader registryReader,
        IWindowsBuildProvider buildProvider,
        RegistryViewKind? nativeRegistryView = null)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
        _buildProvider = buildProvider ?? throw new ArgumentNullException(nameof(buildProvider));
        _nativeRegistryView = nativeRegistryView ?? (Environment.Is64BitOperatingSystem
            ? RegistryViewKind.Registry64
            : RegistryViewKind.Registry32);
    }

    public WindowsContextMenuModeStatus GetStatus()
    {
        var build = _buildProvider.Build;
        if (build < MinimumWindows11Build)
        {
            return new WindowsContextMenuModeStatus(
                WindowsContextMenuMode.Unsupported,
                CanChange: false,
                "当前系统不是受支持的 Windows 11 版本。",
                build,
                _nativeRegistryView);
        }

        if (!_registryReader.KeyExists(
                RegistryHiveKind.CurrentUser,
                _nativeRegistryView,
                OverrideKeyPath))
        {
            return new WindowsContextMenuModeStatus(
                WindowsContextMenuMode.Windows11,
                CanChange: true,
                "未检测到经典菜单兼容覆盖。",
                build,
                _nativeRegistryView);
        }

        var valueNames = _registryReader.GetValueNames(
            RegistryHiveKind.CurrentUser,
            _nativeRegistryView,
            OverrideKeyPath);
        var subKeyNames = _registryReader.GetSubKeyNames(
            RegistryHiveKind.CurrentUser,
            _nativeRegistryView,
            OverrideKeyPath);
        var defaultValue = _registryReader.GetValue(
            RegistryHiveKind.CurrentUser,
            _nativeRegistryView,
            OverrideKeyPath,
            valueName: null);
        var clsidValueNames = _registryReader.GetValueNames(
            RegistryHiveKind.CurrentUser,
            _nativeRegistryView,
            OverrideClsidKeyPath);
        var clsidSubKeyNames = _registryReader.GetSubKeyNames(
            RegistryHiveKind.CurrentUser,
            _nativeRegistryView,
            OverrideClsidKeyPath);
        if (valueNames.Count == 1 && valueNames[0].Length == 0 &&
            subKeyNames.Count == 0 &&
            clsidValueNames.Count == 0 &&
            clsidSubKeyNames.Count == 1 &&
            clsidSubKeyNames[0].Equals("InprocServer32", StringComparison.OrdinalIgnoreCase) &&
            defaultValue is { Kind: RegistryValueDataKind.Text, Value: string text } &&
            text.Length == 0)
        {
            return new WindowsContextMenuModeStatus(
                WindowsContextMenuMode.Classic,
                CanChange: true,
                "检测到 RightMenuCheck 支持的经典菜单兼容覆盖。",
                build,
                _nativeRegistryView);
        }

        return new WindowsContextMenuModeStatus(
            WindowsContextMenuMode.Custom,
            CanChange: false,
            "检测到未知的自定义覆盖。为避免删除其他软件或用户的数据，RightMenuCheck 不会修改它。",
            build,
            _nativeRegistryView);
    }

    public WindowsContextMenuModePlan CreatePlan(WindowsContextMenuMode targetMode)
    {
        if (targetMode is not (WindowsContextMenuMode.Windows11 or WindowsContextMenuMode.Classic))
        {
            throw new ArgumentOutOfRangeException(nameof(targetMode), targetMode, null);
        }

        var current = GetStatus();
        if (!current.CanChange)
        {
            return Blocked(targetMode, current, current.Detail);
        }

        if (current.Mode == targetMode)
        {
            return new WindowsContextMenuModePlan(
                targetMode,
                current,
                IsSupported: true,
                IsNoChange: true,
                BlockReason: null,
                "当前模式无需更改。",
                MutationPlan: null);
        }

        var source = new RegistrySource(
            RegistryHiveKind.CurrentUser,
            _nativeRegistryView,
            targetMode == WindowsContextMenuMode.Classic
                ? OverrideKeyPath
                : OverrideClsidKeyPath);
        var mutation = targetMode == WindowsContextMenuMode.Classic
            ? new RegistryMutation(
                RegistryMutationKind.SetValue,
                source,
                ValueName: null,
                new RegistryValueSnapshot(
                    string.Empty,
                    BackupRegistryValueKind.Text,
                    Text: string.Empty,
                    TextItems: null,
                    Base64Data: null,
                    NumericValue: null),
                KeyTree: null)
            : new RegistryMutation(
                RegistryMutationKind.DeleteKeyTree,
                source,
                ValueName: null,
                Value: null,
                KeyTree: null);
        var targetText = targetMode == WindowsContextMenuMode.Classic
            ? "经典完整菜单"
            : "Windows 11 简洁菜单";
        return new WindowsContextMenuModePlan(
            targetMode,
            current,
            IsSupported: true,
            IsNoChange: false,
            BlockReason: null,
            $"只修改当前用户的兼容覆盖，切换为{targetText}。需要重启 Explorer 后生效。",
            new RegistryMutationPlan(
                Guid.NewGuid(),
                $"Switch Windows context menu to {targetMode}",
                BackupPath: string.Empty,
                [mutation]));
    }

    private static WindowsContextMenuModePlan Blocked(
        WindowsContextMenuMode targetMode,
        WindowsContextMenuModeStatus current,
        string reason) => new(
        targetMode,
        current,
        IsSupported: false,
        IsNoChange: false,
        reason,
        reason,
        MutationPlan: null);
}

public static class SystemExplorerRestarter
{
    private static readonly TimeSpan GracefulExitPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);

    public static async Task<ExplorerRestartResult> RestartAsync(
        CancellationToken cancellationToken)
    {
        var explorerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");
        var sessionId = Process.GetCurrentProcess().SessionId;
        var processes = GetMatchingProcesses(explorerPath, sessionId);
        foreach (var process in processes)
        {
            _ = process.CloseMainWindow();
        }

        if (processes.Count > 0)
        {
            await Task.Delay(GracefulExitPeriod, cancellationToken).ConfigureAwait(false);
        }

        var stopFailed = false;
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                }

                if (!process.WaitForExit(milliseconds: 5000))
                {
                    stopFailed = true;
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
                stopFailed = true;
            }
            finally
            {
                process.Dispose();
            }
        }

        if (stopFailed)
        {
            return new ExplorerRestartResult(
                Succeeded: false,
                processes.Count,
                NewProcessId: null,
                "至少一个 Explorer 进程未能确认退出；未启动替代进程。请从任务管理器完成重启。");
        }

        var deadline = DateTimeOffset.UtcNow + StartupTimeout;
        Process? replacement = null;
        var startAttempted = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            replacement = GetMatchingProcesses(explorerPath, sessionId).FirstOrDefault();
            if (replacement is not null)
            {
                break;
            }

            if (!startAttempted &&
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8) >= deadline)
            {
                startAttempted = true;
                _ = Process.Start(new ProcessStartInfo
                {
                    FileName = explorerPath,
                    UseShellExecute = true,
                });
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        }

        if (replacement is null)
        {
            return new ExplorerRestartResult(
                Succeeded: false,
                processes.Count,
                NewProcessId: null,
                "Explorer 已关闭，但未在超时内确认新的 Explorer 进程。请从任务管理器运行 explorer.exe。");
        }

        try
        {
            return new ExplorerRestartResult(
                Succeeded: true,
                processes.Count,
                replacement.Id,
                "Explorer 已重新启动。");
        }
        finally
        {
            replacement.Dispose();
        }
    }

    private static List<Process> GetMatchingProcesses(string expectedPath, int sessionId)
    {
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                if (process.SessionId == sessionId &&
                    process.MainModule?.FileName is { } actualPath &&
                    Path.GetFullPath(actualPath).Equals(
                        Path.GetFullPath(expectedPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(process);
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }

            process.Dispose();
        }

        return result;
    }
}

public interface IWindowsContextMenuModeService
{
    WindowsContextMenuModeStatus GetStatus();

    WindowsContextMenuModePlan Preview(WindowsContextMenuMode targetMode);

    Task<WindowsContextMenuModeChangeResult> ApplyAsync(
        WindowsContextMenuMode targetMode,
        CancellationToken cancellationToken);

    Task<ExplorerRestartResult> RestartExplorerAsync(CancellationToken cancellationToken);
}

public sealed class WindowsContextMenuModeService : IWindowsContextMenuModeService
{
    private readonly WindowsContextMenuModePlanner _planner;
    private readonly RegistryTransactionExecutor _transaction;
    private readonly IAppLogger _logger;

    public WindowsContextMenuModeService(IAppLogger? logger = null)
    {
        _logger = logger ?? NullAppLogger.Instance;
        var registryReader = new SystemRegistryReader();
        var snapshotReader = new RegistrySnapshotReader(
            registryReader,
            new SystemRegistrySecurityDescriptorReader());
        var journalDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RightMenuCheck",
            "Journals");
        _planner = new WindowsContextMenuModePlanner(
            registryReader,
            new SystemWindowsBuildProvider());
        _transaction = new RegistryTransactionExecutor(
            registryReader,
            snapshotReader,
            new SystemRegistryWriter(),
            new FileRegistryActionJournalStore(journalDirectory));
    }

    public WindowsContextMenuModeStatus GetStatus() => _planner.GetStatus();

    public WindowsContextMenuModePlan Preview(WindowsContextMenuMode targetMode) =>
        _planner.CreatePlan(targetMode);

    public async Task<WindowsContextMenuModeChangeResult> ApplyAsync(
        WindowsContextMenuMode targetMode,
        CancellationToken cancellationToken)
    {
        var plan = _planner.CreatePlan(targetMode);
        if (!plan.IsSupported)
        {
            return new WindowsContextMenuModeChangeResult(
                Succeeded: false,
                IsNoChange: false,
                plan.BlockReason ?? "无法更改右键菜单模式。",
                plan.Current,
                JournalPath: null);
        }

        if (plan.IsNoChange)
        {
            return new WindowsContextMenuModeChangeResult(
                Succeeded: true,
                IsNoChange: true,
                "当前右键菜单模式无需更改。",
                plan.Current,
                JournalPath: null);
        }

        _logger.Log(
            AppLogLevel.Warning,
            "windows_context_menu_mode.change_started",
            "Windows context-menu mode change started.",
            new Dictionary<string, object?>
            {
                ["from"] = plan.Current.Mode.ToString(),
                ["to"] = targetMode.ToString(),
                ["windowsBuild"] = plan.Current.WindowsBuild,
            });
        var mutation = await _transaction.ExecuteAsync(
                plan.MutationPlan ?? throw new InvalidOperationException("Mode plan has no mutation."),
                cancellationToken)
            .ConfigureAwait(false);
        var status = _planner.GetStatus();
        var succeeded = mutation.Succeeded && status.Mode == targetMode;
        var message = succeeded
            ? "右键菜单模式已写入；重启 Explorer 后生效。"
            : mutation.ErrorMessage ?? "注册表已写入，但模式复核未通过。";
        _logger.Log(
            succeeded ? AppLogLevel.Information : AppLogLevel.Error,
            "windows_context_menu_mode.change_completed",
            message,
            new Dictionary<string, object?>
            {
                ["succeeded"] = succeeded,
                ["rolledBack"] = mutation.RolledBack,
                ["mode"] = status.Mode.ToString(),
                ["journalPath"] = mutation.JournalPath,
            });
        return new WindowsContextMenuModeChangeResult(
            succeeded,
            IsNoChange: false,
            message,
            status,
            mutation.JournalPath);
    }

    public async Task<ExplorerRestartResult> RestartExplorerAsync(
        CancellationToken cancellationToken)
    {
        _logger.Log(
            AppLogLevel.Warning,
            "explorer.restart_started",
            "Explorer restart started for context-menu mode activation.");
        var result = await SystemExplorerRestarter
            .RestartAsync(cancellationToken)
            .ConfigureAwait(false);
        _logger.Log(
            result.Succeeded ? AppLogLevel.Information : AppLogLevel.Error,
            "explorer.restart_completed",
            result.Message,
            new Dictionary<string, object?>
            {
                ["succeeded"] = result.Succeeded,
                ["closedProcessCount"] = result.ClosedProcessCount,
                ["newProcessId"] = result.NewProcessId,
            });
        return result;
    }
}
