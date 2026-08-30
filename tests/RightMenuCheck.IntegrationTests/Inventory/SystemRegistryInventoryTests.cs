using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Inventory;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.IntegrationTests.Inventory;

public sealed class SystemRegistryInventoryTests
{
    [Fact]
    public void ScanReadsInstalledContextMenusWithoutChangingRegistry()
    {
        var reader = new SystemRegistryReader();
        var scanner = new ContextMenuRegistryScanner(reader);

        var result = scanner.Scan(CancellationToken.None);

        Assert.NotEmpty(result.Registrations);
        Assert.Contains(
            result.Registrations,
            item => item.Kind == ContextMenuRegistrationKind.ClassicContextMenuHandler);
        Assert.Contains(
            result.Registrations,
            item => item.Kind is ContextMenuRegistrationKind.StaticVerb or
                ContextMenuRegistrationKind.ExplorerCommand or
                ContextMenuRegistrationKind.CascadingVerb);
        Assert.Contains(
            result.Registrations,
            item => item.TargetKind == ContextMenuTargetKind.File);
        Assert.Contains(
            result.Registrations,
            item => item.TargetKind is ContextMenuTargetKind.Folder or
                ContextMenuTargetKind.FolderBackground);
        Assert.All(
            result.Registrations,
            item => Assert.StartsWith(
                "Software\\Classes\\",
                item.RegistrationPath,
                StringComparison.OrdinalIgnoreCase));
    }
}
