using RightMenuCheck.Distribution;

namespace RightMenuCheck.Installation;

public sealed record InstallationPaths(
    string InstallDirectory,
    string InstallerCacheDirectory,
    string UninstallerPath,
    string StartMenuShortcutPath,
    string DesktopShortcutPath)
{
    public string ApplicationPath => Path.Combine(
        InstallDirectory,
        UpdateInstallLocations.ApplicationFileName);

    public static InstallationPaths CreateSystem()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var startMenu = Environment.GetFolderPath(
            Environment.SpecialFolder.Programs);
        var desktop = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        var cacheDirectory = Path.Combine(
            localAppData,
            "RightMenuCheck",
            "Installer");
        return new InstallationPaths(
            UpdateInstallLocations.GetPerUserInstallDirectory(),
            cacheDirectory,
            Path.Combine(cacheDirectory, "RightMenuCheck.Uninstaller.exe"),
            Path.Combine(startMenu, "RightMenuCheck.lnk"),
            Path.Combine(desktop, "RightMenuCheck.lnk"));
    }

    public void Validate()
    {
        var install = NormalizeDirectory(InstallDirectory, nameof(InstallDirectory));
        var cache = NormalizeDirectory(
            InstallerCacheDirectory,
            nameof(InstallerCacheDirectory));
        var uninstaller = NormalizeFile(UninstallerPath, nameof(UninstallerPath));
        _ = NormalizeFile(StartMenuShortcutPath, nameof(StartMenuShortcutPath));
        _ = NormalizeFile(DesktopShortcutPath, nameof(DesktopShortcutPath));
        if (Directory.GetParent(install) is null || Directory.GetParent(cache) is null)
        {
            throw new InvalidDataException("Installation paths cannot be filesystem roots.");
        }

        if (IsWithin(cache, install) || IsWithin(install, cache))
        {
            throw new InvalidDataException(
                "Installer cache and application directories must not contain each other.");
        }

        if (!Path.GetDirectoryName(uninstaller)!.Equals(
                cache,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The uninstaller must be stored directly in the installer cache.");
        }
    }

    private static string NormalizeDirectory(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException($"{parameterName} must be an absolute path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static string NormalizeFile(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException($"{parameterName} must be an absolute path.");
        }

        return Path.GetFullPath(value);
    }

    private static bool IsWithin(string candidate, string parent) =>
        candidate.StartsWith(
            parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

public sealed record InstallationOptions(bool CreateDesktopShortcut);

public sealed record InstallationProgress(double Percentage, string Status);

public sealed record InstallationResult(
    string Version,
    string InstallDirectory,
    string ApplicationPath);

public interface IInstallationPayloadSource
{
    string ExpectedVersion { get; }

    string ExpectedPackageSha256 { get; }

    Stream OpenApplicationPackage();

    Stream OpenUninstaller();
}

public interface IInstallIntegrationSnapshot
{
}

public interface IInstallIntegration
{
    Task EnsureApplicationStoppedAsync(
        string applicationPath,
        CancellationToken cancellationToken);

    IInstallIntegrationSnapshot Capture(InstallationPaths paths);

    void Apply(
        InstallationPaths paths,
        string version,
        bool createDesktopShortcut,
        long estimatedSizeKilobytes);

    void Restore(InstallationPaths paths, IInstallIntegrationSnapshot snapshot);
}
