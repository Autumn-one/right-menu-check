using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Metadata;
using RightMenuCheck.Windows.Registry;
using RightMenuCheck.Windows.Tests.Registry;

namespace RightMenuCheck.Windows.Tests.Metadata;

public sealed class ComServerResolverTests
{
    private const RegistryViewKind View = RegistryViewKind.Registry64;
    private const string HandlerClsid = "{11111111-2222-3333-4444-555555555555}";
    private const string TargetClsid = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";

    [Fact]
    public void ResolveClassicPrefersCurrentUserComRegistration()
    {
        var reader = new InMemoryRegistryReader(View);
        SetInProcessServer(
            reader,
            RegistryHiveKind.LocalMachine,
            HandlerClsid,
            "C:\\Machine\\handler.dll");
        SetInProcessServer(
            reader,
            RegistryHiveKind.CurrentUser,
            HandlerClsid,
            "C:\\User\\handler.dll");
        var resolver = new ComServerResolver(reader);

        var component = Assert.Single(resolver.Resolve(CreateRegistryRegistration()));

        var server = Assert.IsType<ComServerRegistration>(component.ComServer);
        Assert.Equal(ComServerKind.InProcess, server.Kind);
        Assert.Equal("C:\\User\\handler.dll", server.ResolvedServerPath);
        Assert.Equal("Apartment", server.ThreadingModel);
        Assert.Equal(RegistryHiveKind.CurrentUser, server.RegistrySource?.Hive);
        Assert.Empty(component.Issues);
    }

    [Fact]
    public void ResolveClassicFollowsTreatAsRegistration()
    {
        var reader = new InMemoryRegistryReader(View);
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            $"Software\\Classes\\CLSID\\{HandlerClsid}\\TreatAs",
            valueName: null,
            TargetClsid);
        SetInProcessServer(
            reader,
            RegistryHiveKind.LocalMachine,
            TargetClsid,
            "C:\\Handlers\\target.dll");
        var resolver = new ComServerResolver(reader);

        var server = Assert.IsType<ComServerRegistration>(
            Assert.Single(resolver.Resolve(CreateRegistryRegistration())).ComServer);

        Assert.Equal(HandlerClsid, server.Clsid);
        Assert.Equal(TargetClsid, server.TreatAsClsid);
        Assert.Equal("C:\\Handlers\\target.dll", server.ResolvedServerPath);
    }

    [Fact]
    public void ResolvePackagedCombinesManifestDirectoryAndDllPath()
    {
        var reader = new InMemoryRegistryReader(View);
        const string packageFullName = "Sample.Package_1.0.0.0_x64__publisher";
        var classPath =
            $"Software\\Classes\\PackagedCom\\Package\\{packageFullName}\\Class\\{HandlerClsid}";
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            classPath,
            "DllPath",
            "ShellExtension.dll");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            classPath,
            "ServerId",
            2,
            RegistryValueDataKind.DWord);
        var resolver = new ComServerResolver(reader);

        var component = Assert.Single(
            resolver.Resolve(CreatePackageRegistration(packageFullName)));

        var server = Assert.IsType<ComServerRegistration>(component.ComServer);
        Assert.Equal(ComServerKind.PackagedInProcess, server.Kind);
        Assert.Equal(
            "C:\\Packages\\Sample\\ShellExtension.dll",
            server.ResolvedServerPath);
        Assert.Equal(2, server.PackageServerId);
        Assert.Equal(packageFullName, server.PackageFullName);
    }

    [Fact]
    public void ResolvePackagedFollowsServerIdToExecutable()
    {
        var reader = new InMemoryRegistryReader(View);
        const string packageFullName = "Sample.Package_1.0.0.0_x64__publisher";
        var packagePath =
            $"Software\\Classes\\PackagedCom\\Package\\{packageFullName}";
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            $"{packagePath}\\Class\\{HandlerClsid}",
            "ServerId",
            7,
            RegistryValueDataKind.DWord);
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            $"{packagePath}\\Server\\7",
            "Executable",
            "Server.exe");
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            $"{packagePath}\\Server\\7",
            "DisplayName",
            "Sample server");
        var resolver = new ComServerResolver(reader);

        var server = Assert.IsType<ComServerRegistration>(
            Assert.Single(resolver.Resolve(CreatePackageRegistration(packageFullName))).ComServer);

        Assert.Equal(ComServerKind.PackagedLocalServer, server.Kind);
        Assert.Equal("C:\\Packages\\Sample\\Server.exe", server.ResolvedServerPath);
        Assert.Equal("Sample server", server.DisplayName);
    }

    [Fact]
    public void ResolvePackagedUsesNativeDllPathForNeutralPackage()
    {
        var reader = new InMemoryRegistryReader(View);
        const string packageFullName = "Sample.Package_1.0.0.0_neutral__publisher";
        var classPath =
            $"Software\\Classes\\PackagedCom\\Package\\{packageFullName}\\Class\\{HandlerClsid}";
        reader.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            classPath,
            "DllPath_x64",
            "NeutralShellExtension.dll");
        var registration = CreatePackageRegistration(packageFullName) with
        {
            Source = new PackageContextMenuSource(
                "Sample.Package",
                packageFullName,
                "Sample.Package_publisher",
                "App",
                PackageArchitectureKind.Neutral,
                "C:\\Packages\\Sample\\AppxManifest.xml"),
        };
        var resolver = new ComServerResolver(reader);

        var server = Assert.IsType<ComServerRegistration>(
            Assert.Single(resolver.Resolve(registration)).ComServer);

        Assert.Equal(
            "C:\\Packages\\Sample\\NeutralShellExtension.dll",
            server.ResolvedServerPath);
    }

    [Fact]
    public void ResolveReturnsEveryDistinctHandlerRole()
    {
        var reader = new InMemoryRegistryReader(View);
        SetInProcessServer(
            reader,
            RegistryHiveKind.LocalMachine,
            HandlerClsid,
            "C:\\Handlers\\menu.dll");
        SetInProcessServer(
            reader,
            RegistryHiveKind.LocalMachine,
            TargetClsid,
            "C:\\Handlers\\state.dll");
        var delegateClsid = "{BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF}";
        SetInProcessServer(
            reader,
            RegistryHiveKind.LocalMachine,
            delegateClsid,
            "C:\\Handlers\\delegate.dll");
        var registration = CreateRegistryRegistration() with
        {
            Kind = ContextMenuRegistrationKind.ExplorerCommand,
            CommandStateHandlerClsid = TargetClsid,
            DelegateExecuteClsid = delegateClsid,
        };
        var resolver = new ComServerResolver(reader);

        var components = resolver.Resolve(registration);

        Assert.Equal(3, components.Count);
        Assert.Contains(components, item => item.Role == HandlerComponentRole.ExplorerCommand);
        Assert.Contains(components, item => item.Role == HandlerComponentRole.CommandStateHandler);
        Assert.Contains(components, item => item.Role == HandlerComponentRole.DelegateExecute);
    }

    private static ContextMenuRegistration CreateRegistryRegistration() => new()
    {
        Id = "test-registration",
        Source = new RegistryContextMenuSource(new RegistrySource(
            RegistryHiveKind.LocalMachine,
            View,
            "Software\\Classes\\*\\shellex\\ContextMenuHandlers\\Test")),
        ClassPath = "*",
        RegistrationPath = "Software\\Classes\\*\\shellex\\ContextMenuHandlers\\Test",
        CanonicalName = "Test",
        DisplayName = "Test",
        TargetKind = ContextMenuTargetKind.File,
        Kind = ContextMenuRegistrationKind.ClassicContextMenuHandler,
        HandlerClsid = HandlerClsid,
    };

    private static ContextMenuRegistration CreatePackageRegistration(string packageFullName) => new()
    {
        Id = "package-registration",
        Source = new PackageContextMenuSource(
            "Sample.Package",
            packageFullName,
            "Sample.Package_publisher",
            "App",
            PackageArchitectureKind.X64,
            "C:\\Packages\\Sample\\AppxManifest.xml"),
        ClassPath = "*",
        RegistrationPath = "Applications\\App\\Extensions\\windows.fileExplorerContextMenus\\*\\Test",
        CanonicalName = "Test",
        DisplayName = "Test",
        TargetKind = ContextMenuTargetKind.File,
        Kind = ContextMenuRegistrationKind.PackagedExplorerCommand,
        HandlerClsid = HandlerClsid,
    };

    private static void SetInProcessServer(
        InMemoryRegistryReader reader,
        RegistryHiveKind hive,
        string clsid,
        string serverPath)
    {
        var path = $"Software\\Classes\\CLSID\\{clsid}\\InprocServer32";
        reader.SetValue(hive, View, path, valueName: null, serverPath);
        reader.SetValue(hive, View, path, "ThreadingModel", "Apartment");
    }
}
