using System.ComponentModel;
using System.Diagnostics;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Metadata;
using Windows.Management.Deployment;

namespace RightMenuCheck.Windows.Management;

public enum ApplicationUninstallMethod
{
    Unsupported,
    PackageCurrentUser,
    MsiProductCode,
    VendorExecutable,
}

public sealed record ApplicationUninstallPlan(
    bool IsSupported,
    ApplicationUninstallMethod Method,
    ApplicationOwnerMetadata Owner,
    string? ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? PackageFullName,
    bool RequiresElevation,
    string ImpactDescription,
    string? BlockReason);

public sealed record ApplicationUninstallExecutionResult(
    ApplicationUninstallPlan Plan,
    bool Started,
    bool Completed,
    bool Cancelled,
    int? ExitCode,
    string? ErrorType,
    string? ErrorMessage);

public interface IApplicationUninstallPlanner
{
    ApplicationUninstallPlan CreatePlan(
        ApplicationOwnerMetadata owner,
        bool allowSystemProtected);
}

public sealed class ApplicationUninstallPlanner : IApplicationUninstallPlanner
{
    private static readonly HashSet<string> ForbiddenIntermediaries = new(
        [
            "cmd.exe",
            "powershell.exe",
            "pwsh.exe",
            "rundll32.exe",
            "mshta.exe",
            "wscript.exe",
            "cscript.exe",
        ],
        StringComparer.OrdinalIgnoreCase);

    public ApplicationUninstallPlan CreatePlan(
        ApplicationOwnerMetadata owner,
        bool allowSystemProtected)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.IsSystemProtected && !allowSystemProtected)
        {
            return Unsupported(owner, "Microsoft or system-owned applications are protected by default.");
        }

        if (owner.Kind == ApplicationOwnerKind.Package &&
            !string.IsNullOrWhiteSpace(owner.PackageFullName))
        {
            return new ApplicationUninstallPlan(
                IsSupported: true,
                ApplicationUninstallMethod.PackageCurrentUser,
                owner,
                ExecutablePath: null,
                Arguments: [],
                owner.PackageFullName,
                RequiresElevation: false,
                "Removes the entire current-user package, not only its context-menu command. The registry backup cannot reinstall it.",
                BlockReason: null);
        }

        if (owner.Kind == ApplicationOwnerKind.InstalledApplication &&
            owner.IsWindowsInstaller &&
            owner.ProductCode is { } productCode &&
            Guid.TryParse(productCode, out var productGuid))
        {
            var normalizedProductCode = productGuid.ToString("B").ToUpperInvariant();
            return new ApplicationUninstallPlan(
                IsSupported: true,
                ApplicationUninstallMethod.MsiProductCode,
                owner,
                Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                ["/x", normalizedProductCode],
                PackageFullName: null,
                RequiresElevation: true,
                "Launches Windows Installer for the exact ProductCode and removes the entire MSI product.",
                BlockReason: null);
        }

        return CreateVendorPlan(owner);
    }

    private static ApplicationUninstallPlan CreateVendorPlan(ApplicationOwnerMetadata owner)
    {
        if (owner.Kind != ApplicationOwnerKind.InstalledApplication ||
            owner.Confidence < OwnershipConfidence.High ||
            string.IsNullOrWhiteSpace(owner.UninstallString) ||
            string.IsNullOrWhiteSpace(owner.InstallLocation))
        {
            return Unsupported(
                owner,
                "No exact package, MSI ProductCode, or high-confidence vendor uninstall path is available.");
        }

        IReadOnlyList<string> parsed;
        try
        {
            parsed = CommandLineParser.Parse(owner.UninstallString);
        }
        catch (Win32Exception exception)
        {
            return Unsupported(owner, $"Vendor uninstall command could not be parsed: {exception.Message}");
        }

        if (parsed.Count == 0 || !Path.IsPathFullyQualified(parsed[0]))
        {
            return Unsupported(owner, "Vendor uninstall executable path is not absolute.");
        }

        var executable = Path.GetFullPath(parsed[0]);
        var installLocation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(owner.InstallLocation)));
        if (!File.Exists(executable) || !IsPathWithin(executable, installLocation) ||
            ForbiddenIntermediaries.Contains(Path.GetFileName(executable)))
        {
            return Unsupported(
                owner,
                "Vendor uninstaller is missing, outside InstallLocation, or uses a forbidden command intermediary.");
        }

        return new ApplicationUninstallPlan(
            IsSupported: true,
            ApplicationUninstallMethod.VendorExecutable,
            owner,
            executable,
            parsed.Skip(1).ToArray(),
            PackageFullName: null,
            RequiresElevation: false,
            "Launches the vendor executable directly with a structured argument list and removes the entire application.",
            BlockReason: null);
    }

    private static bool IsPathWithin(string candidatePath, string rootPath)
    {
        var relative = Path.GetRelativePath(rootPath, candidatePath);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static ApplicationUninstallPlan Unsupported(
        ApplicationOwnerMetadata owner,
        string reason) =>
        new(
            IsSupported: false,
            ApplicationUninstallMethod.Unsupported,
            owner,
            ExecutablePath: null,
            Arguments: [],
            PackageFullName: null,
            RequiresElevation: false,
            ImpactDescription: reason,
            BlockReason: reason);
}

public sealed record PackageUninstallResult(bool Succeeded, string? ErrorType, string? ErrorMessage);

public interface IPackageUninstaller
{
    Task<PackageUninstallResult> RemoveCurrentUserPackageAsync(
        string packageFullName,
        CancellationToken cancellationToken);
}

public sealed class SystemPackageUninstaller : IPackageUninstaller
{
    public async Task<PackageUninstallResult> RemoveCurrentUserPackageAsync(
        string packageFullName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFullName);
        var operation = new PackageManager().RemovePackageAsync(packageFullName);
        var result = await operation.AsTask(cancellationToken).ConfigureAwait(false);
        return result.ExtendedErrorCode is null || result.ExtendedErrorCode.HResult == 0
            ? new PackageUninstallResult(Succeeded: true, ErrorType: null, ErrorMessage: null)
            : new PackageUninstallResult(
                Succeeded: false,
                result.ExtendedErrorCode.GetType().Name,
                result.ErrorText);
    }
}

public sealed record ProcessUninstallResult(bool Started, int? ExitCode);

public interface IProcessUninstallLauncher
{
    Task<ProcessUninstallResult> LaunchAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        bool requestElevation,
        CancellationToken cancellationToken);
}

public sealed class SystemProcessUninstallLauncher : IProcessUninstallLauncher
{
    public async Task<ProcessUninstallResult> LaunchAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        bool requestElevation,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        if (requestElevation)
        {
            startInfo.Verb = "runas";
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new ProcessUninstallResult(Started: false, ExitCode: null);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessUninstallResult(Started: true, process.ExitCode);
    }
}

public sealed class ApplicationUninstallService
{
    private readonly IPackageUninstaller _packageUninstaller;
    private readonly IProcessUninstallLauncher _processLauncher;

    public ApplicationUninstallService(
        IPackageUninstaller packageUninstaller,
        IProcessUninstallLauncher processLauncher)
    {
        _packageUninstaller = packageUninstaller ??
                              throw new ArgumentNullException(nameof(packageUninstaller));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
    }

    public async Task<ApplicationUninstallExecutionResult> ExecuteAsync(
        ApplicationUninstallPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsSupported)
        {
            throw new InvalidOperationException(plan.BlockReason);
        }

        try
        {
            if (plan.Method == ApplicationUninstallMethod.PackageCurrentUser)
            {
                var packageResult = await _packageUninstaller
                    .RemoveCurrentUserPackageAsync(plan.PackageFullName!, cancellationToken)
                    .ConfigureAwait(false);
                return new ApplicationUninstallExecutionResult(
                    plan,
                    Started: true,
                    Completed: packageResult.Succeeded,
                    Cancelled: false,
                    ExitCode: null,
                    packageResult.ErrorType,
                    packageResult.ErrorMessage);
            }

            var processResult = await _processLauncher
                .LaunchAsync(
                    plan.ExecutablePath!,
                    plan.Arguments,
                    plan.RequiresElevation,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ApplicationUninstallExecutionResult(
                plan,
                processResult.Started,
                Completed: processResult.ExitCode == 0,
                Cancelled: false,
                processResult.ExitCode,
                processResult.ExitCode is 0 ? null : "UninstallerExitCode",
                processResult.ExitCode is 0
                    ? null
                    : $"The uninstaller exited with code {processResult.ExitCode}.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return new ApplicationUninstallExecutionResult(
                plan,
                Started: false,
                Completed: false,
                Cancelled: true,
                ExitCode: null,
                "UacCancelled",
                "The administrator consent prompt was cancelled.");
        }
    }
}

public static class UninstallResidualDetector
{
    public static IReadOnlyList<ContextMenuRegistrationMetadata> FindResiduals(
        ApplicationOwnerMetadata owner,
        IEnumerable<ContextMenuRegistrationMetadata> registrations)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(registrations);

        return registrations.Where(registration => IsSameOwner(owner, registration.Owner)).ToArray();
    }

    private static bool IsSameOwner(
        ApplicationOwnerMetadata expected,
        ApplicationOwnerMetadata? actual)
    {
        if (actual is null)
        {
            return false;
        }

        if (expected.PackageFullName is { } packageFullName)
        {
            return packageFullName.Equals(actual.PackageFullName, StringComparison.OrdinalIgnoreCase);
        }

        if (expected.ProductCode is { } productCode)
        {
            return productCode.Equals(actual.ProductCode, StringComparison.OrdinalIgnoreCase);
        }

        return expected.Confidence >= OwnershipConfidence.High &&
               expected.DisplayName.Equals(actual.DisplayName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(expected.Publisher, actual.Publisher, StringComparison.OrdinalIgnoreCase);
    }
}
