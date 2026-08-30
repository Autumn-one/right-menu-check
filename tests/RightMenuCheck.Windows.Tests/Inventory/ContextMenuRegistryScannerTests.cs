using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Inventory;
using RightMenuCheck.Windows.Tests.Registry;

namespace RightMenuCheck.Windows.Tests.Inventory;

public sealed class ContextMenuRegistryScannerTests
{
    private const RegistryViewKind View = RegistryViewKind.Registry64;
    private const string TestClsid = "{11111111-2222-3333-4444-555555555555}";

    [Fact]
    public void ScanFindsClassicHandlerAndEffectiveBlockedState()
    {
        var reader = new InMemoryRegistryReader(View);
        var handlerPath =
            "Software\\Classes\\*\\shellex\\ContextMenuHandlers\\Slow.Handler";
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            handlerPath,
            valueName: null,
            TestClsid);
        reader.SetValue(
            RegistryHiveKind.CurrentUser,
            View,
            "Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions\\Blocked",
            TestClsid,
            "Disabled for test");

        var result = new ContextMenuRegistryScanner(reader).Scan();

        var registration = Assert.Single(result.Registrations);
        Assert.Equal(ContextMenuRegistrationKind.ClassicContextMenuHandler, registration.Kind);
        Assert.Equal(ContextMenuTargetKind.File, registration.TargetKind);
        Assert.Equal(TestClsid, registration.HandlerClsid);
        Assert.True(registration.Status.HasFlag(ContextMenuRegistrationStatus.Blocked));
        Assert.False(registration.IsVisibleByDefault);
    }

    [Fact]
    public void ScanPreservesNestedCascadeParentRelationship()
    {
        var reader = new InMemoryRegistryReader(View);
        const string parentPath =
            "Software\\Classes\\Directory\\Background\\shell\\Diagnostics";
        const string childCommandPath =
            "Software\\Classes\\Directory\\Background\\shell\\Diagnostics\\shell\\Inspect\\command";
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            parentPath,
            "MUIVerb",
            "Diagnostics");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            childCommandPath,
            valueName: null,
            "inspect.exe \"%V\"");

        var result = new ContextMenuRegistryScanner(reader).Scan();

        var parent = Assert.Single(
            result.Registrations,
            item => item.CanonicalName == "Diagnostics");
        var child = Assert.Single(
            result.Registrations,
            item => item.CanonicalName == "Inspect");
        Assert.Equal(ContextMenuRegistrationKind.CascadingVerb, parent.Kind);
        Assert.Equal(ContextMenuRegistrationKind.StaticVerb, child.Kind);
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal(ContextMenuTargetKind.FolderBackground, child.TargetKind);
        Assert.Equal("inspect.exe \"%V\"", child.Command);
    }

    [Fact]
    public void ScanDiscoversExtensionProgIdAndSystemFileAssociationMenus()
    {
        var reader = new InMemoryRegistryReader(View);
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            "Software\\Classes\\.sample",
            valueName: null,
            "samplefile");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            "Software\\Classes\\.sample",
            "PerceivedType",
            "image");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            "Software\\Classes\\.sample\\OpenWithProgids",
            "sample.alt",
            string.Empty);
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            "Software\\Classes\\.sample\\shell\\inspect\\command",
            valueName: null,
            "sample-inspector.exe \"%1\"");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            "Software\\Classes\\samplefile\\shell\\convert\\command",
            valueName: null,
            "sample-converter.exe \"%1\"");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            "Software\\Classes\\sample.alt\\shell\\alternate\\command",
            valueName: null,
            "sample-alternate.exe \"%1\"");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            "Software\\Classes\\SystemFileAssociations\\image\\shell\\rotate\\command",
            valueName: null,
            "image-tool.exe \"%1\"");

        var result = new ContextMenuRegistryScanner(reader).Scan();

        Assert.Contains(result.Registrations, item => item.ClassPath == ".sample");
        Assert.Contains(result.Registrations, item => item.ClassPath == "samplefile");
        Assert.Contains(result.Registrations, item => item.ClassPath == "sample.alt");
        Assert.Contains(
            result.Registrations,
            item => item.ClassPath == "SystemFileAssociations\\image");
        Assert.All(
            result.Registrations,
            item => Assert.Equal(ContextMenuTargetKind.FileType, item.TargetKind));
        Assert.Contains(
            result.Registrations.Single(item => item.ClassPath == "samplefile").FileAssociations,
            association => association.Extension == ".sample" &&
                           association.DefaultProgId == "samplefile");
        Assert.Contains(
            result.Registrations.Single(item =>
                item.ClassPath == "SystemFileAssociations\\image").FileAssociations,
            association => association.Extension == ".sample" &&
                           association.PerceivedType == "image");
        Assert.Contains(
            result.Registrations.Single(item => item.ClassPath == "sample.alt").FileAssociations,
            association => association.Extension == ".sample" &&
                           association.OpenWithProgIds.Contains("sample.alt"));
    }

    [Fact]
    public void ScanClassifiesExplorerDelegateAndHiddenVerbStates()
    {
        var reader = new InMemoryRegistryReader(View);
        const string explorerPath = "Software\\Classes\\*\\shell\\modern";
        const string delegateCommandPath = "Software\\Classes\\*\\shell\\delegate\\command";
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            explorerPath,
            "ExplorerCommandHandler",
            TestClsid);
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            explorerPath,
            "Extended",
            string.Empty);
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            delegateCommandPath,
            "DelegateExecute",
            TestClsid);
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            "Software\\Classes\\*\\shell\\delegate",
            "LegacyDisable",
            string.Empty);

        var result = new ContextMenuRegistryScanner(reader).Scan();

        var explorer = Assert.Single(
            result.Registrations,
            item => item.CanonicalName == "modern");
        var delegateVerb = Assert.Single(
            result.Registrations,
            item => item.CanonicalName == "delegate");
        Assert.Equal(ContextMenuRegistrationKind.ExplorerCommand, explorer.Kind);
        Assert.True(explorer.Status.HasFlag(ContextMenuRegistrationStatus.ExtendedOnly));
        Assert.Equal(ContextMenuRegistrationKind.DelegateExecuteVerb, delegateVerb.Kind);
        Assert.True(delegateVerb.Status.HasFlag(ContextMenuRegistrationStatus.LegacyDisabled));
    }

    [Fact]
    public void ScanKeepsBothSourcesAndMarksMachineEntryWhenUserOverrideExists()
    {
        var reader = new InMemoryRegistryReader(View);
        const string commandPath = "Software\\Classes\\*\\shell\\share\\command";
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            commandPath,
            valueName: null,
            "machine-share.exe \"%1\"");
        reader.SetValue(
            RegistryHiveKind.CurrentUser,
            View,
            commandPath,
            valueName: null,
            "user-share.exe \"%1\"");

        var result = new ContextMenuRegistryScanner(reader).Scan();

        var entries = result.Registrations.Where(item => item.CanonicalName == "share").ToArray();
        Assert.Equal(2, entries.Length);
        var machine = Assert.Single(entries, item =>
            Assert.IsType<RegistryContextMenuSource>(item.Source).Location.Hive ==
            RegistryHiveKind.LocalMachine);
        var user = Assert.Single(entries, item =>
            Assert.IsType<RegistryContextMenuSource>(item.Source).Location.Hive ==
            RegistryHiveKind.CurrentUser);
        Assert.True(
            machine.Status.HasFlag(ContextMenuRegistrationStatus.CurrentUserOverridePresent));
        Assert.False(
            user.Status.HasFlag(ContextMenuRegistrationStatus.CurrentUserOverridePresent));
        Assert.Equal("machine-share.exe \"%1\"", machine.Command);
        Assert.Equal("user-share.exe \"%1\"", user.Command);
    }
}
