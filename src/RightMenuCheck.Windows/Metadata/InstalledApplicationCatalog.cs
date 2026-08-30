using System.Security;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Windows.Metadata;

public sealed record InstalledApplicationInfo(
    string KeyName,
    string DisplayName,
    string? Publisher,
    string? DisplayVersion,
    string? InstallLocation,
    string? DisplayIcon,
    string? UninstallString,
    string? QuietUninstallString,
    string? ProductCode,
    bool WindowsInstaller,
    bool SystemComponent,
    bool NoRemove,
    RegistrySource Source);

public sealed record InstalledApplicationCatalogResult(
    IReadOnlyList<InstalledApplicationInfo> Applications,
    IReadOnlyList<MetadataIssue> Issues);

public interface IInstalledApplicationCatalog
{
    InstalledApplicationCatalogResult GetApplications();
}

public sealed class RegistryInstalledApplicationCatalog : IInstalledApplicationCatalog
{
    private const string UninstallRoot =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
    private readonly IRegistryReader _registryReader;

    public RegistryInstalledApplicationCatalog(IRegistryReader registryReader)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
    }

    public InstalledApplicationCatalogResult GetApplications()
    {
        var applications = new List<InstalledApplicationInfo>();
        var issues = new List<MetadataIssue>();

        foreach (var view in _registryReader.AvailableViews)
        {
            foreach (var hive in Enum.GetValues<RegistryHiveKind>())
            {
                foreach (var keyName in ReadSubKeyNames(hive, view, UninstallRoot, issues))
                {
                    var keyPath = Combine(UninstallRoot, keyName);
                    var displayName = ReadString(hive, view, keyPath, "DisplayName", issues);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    var windowsInstaller = ReadBoolean(
                        hive,
                        view,
                        keyPath,
                        "WindowsInstaller",
                        issues);
                    var productCode = windowsInstaller
                        ? ClsidUtilities.Normalize(keyName)
                        : null;

                    applications.Add(new InstalledApplicationInfo(
                        keyName,
                        displayName,
                        ReadString(hive, view, keyPath, "Publisher", issues),
                        ReadString(hive, view, keyPath, "DisplayVersion", issues),
                        ReadString(hive, view, keyPath, "InstallLocation", issues),
                        ReadString(hive, view, keyPath, "DisplayIcon", issues),
                        ReadString(hive, view, keyPath, "UninstallString", issues),
                        ReadString(hive, view, keyPath, "QuietUninstallString", issues),
                        productCode,
                        windowsInstaller,
                        ReadBoolean(hive, view, keyPath, "SystemComponent", issues),
                        ReadBoolean(hive, view, keyPath, "NoRemove", issues),
                        new RegistrySource(hive, view, keyPath)));
                }
            }
        }

        var ordered = applications
            .OrderBy(static item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static item => item.Source.Hive)
            .ThenBy(static item => item.Source.View)
            .ThenBy(static item => item.KeyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new InstalledApplicationCatalogResult(ordered, issues);
    }

    private IReadOnlyList<string> ReadSubKeyNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        List<MetadataIssue> issues)
    {
        try
        {
            return _registryReader.GetSubKeyNames(hive, view, keyPath);
        }
        catch (UnauthorizedAccessException exception)
        {
            AddIssue(exception);
        }
        catch (SecurityException exception)
        {
            AddIssue(exception);
        }
        catch (IOException exception)
        {
            AddIssue(exception);
        }

        return [];

        void AddIssue(Exception exception)
        {
            issues.Add(new MetadataIssue(
                keyPath,
                "EnumerateUninstallEntries",
                exception.GetType().Name,
                exception.Message));
        }
    }

    private string? ReadString(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string valueName,
        List<MetadataIssue> issues)
    {
        var value = ReadValue(hive, view, keyPath, valueName, issues)?.Value;
        return value switch
        {
            null => null,
            string text => text,
            _ => value.ToString(),
        };
    }

    private bool ReadBoolean(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string valueName,
        List<MetadataIssue> issues)
    {
        var value = ReadValue(hive, view, keyPath, valueName, issues)?.Value;
        return value switch
        {
            int intValue => intValue != 0,
            long longValue => longValue != 0,
            string text when int.TryParse(
                text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed != 0,
            _ => false,
        };
    }

    private RegistryValueData? ReadValue(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string valueName,
        List<MetadataIssue> issues)
    {
        try
        {
            return _registryReader.GetValue(hive, view, keyPath, valueName);
        }
        catch (UnauthorizedAccessException exception)
        {
            AddIssue(exception);
        }
        catch (SecurityException exception)
        {
            AddIssue(exception);
        }
        catch (IOException exception)
        {
            AddIssue(exception);
        }

        return null;

        void AddIssue(Exception exception)
        {
            issues.Add(new MetadataIssue(
                keyPath,
                $"ReadUninstallValue:{valueName}",
                exception.GetType().Name,
                exception.Message));
        }
    }

    private static string Combine(params string[] parts) =>
        string.Join('\\', parts.Where(static part => !string.IsNullOrWhiteSpace(part)))
            .Replace('/', '\\')
            .Trim('\\');
}
