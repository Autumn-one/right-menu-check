using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Metadata;
using RightMenuCheck.Windows.Registry;
using RightMenuCheck.Windows.Tests.Registry;

namespace RightMenuCheck.Windows.Tests.Metadata;

public sealed class InstalledApplicationCatalogTests
{
    private const string ProductCode = "{11111111-2222-3333-4444-555555555555}";

    [Fact]
    public void GetApplicationsPreservesUninstallSourceAndMsiMetadata()
    {
        var reader = new InMemoryRegistryReader(
            RegistryViewKind.Registry64,
            RegistryViewKind.Registry32);
        var keyPath =
            $"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{ProductCode}";
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            RegistryViewKind.Registry64,
            keyPath,
            "DisplayName",
            "Sample Application");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            RegistryViewKind.Registry64,
            keyPath,
            "Publisher",
            "Sample Publisher");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            RegistryViewKind.Registry64,
            keyPath,
            "InstallLocation",
            "C:\\Apps\\Sample");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            RegistryViewKind.Registry64,
            keyPath,
            "UninstallString",
            $"msiexec.exe /x {ProductCode}");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            RegistryViewKind.Registry64,
            keyPath,
            "WindowsInstaller",
            1,
            RegistryValueDataKind.DWord);
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            RegistryViewKind.Registry64,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Hidden",
            "Publisher",
            "No display name");
        var catalog = new RegistryInstalledApplicationCatalog(reader);

        var result = catalog.GetApplications();

        var application = Assert.Single(result.Applications);
        Assert.Equal("Sample Application", application.DisplayName);
        Assert.Equal(ProductCode, application.ProductCode);
        Assert.True(application.WindowsInstaller);
        Assert.Equal(RegistryHiveKind.LocalMachine, application.Source.Hive);
        Assert.Equal(RegistryViewKind.Registry64, application.Source.View);
        Assert.Equal($"msiexec.exe /x {ProductCode}", application.UninstallString);
        Assert.Empty(result.Issues);
    }
}
