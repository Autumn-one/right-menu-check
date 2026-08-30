using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Metadata;
using RightMenuCheck.Windows.Packages;

namespace RightMenuCheck.Windows.Tests.Metadata;

public sealed class ApplicationOwnershipResolverTests
{
    [Fact]
    public void ResolveUsesExactPackageFullName()
    {
        const string fullName = "Sample.Package_1.0.0.0_x64__publisher";
        var package = new InstalledPackageInfo(
            "Sample.Package",
            fullName,
            "Sample.Package_publisher",
            "Sample App",
            "Sample Publisher",
            "1.0.0.0",
            PackageArchitectureKind.X64,
            "C:\\Packages\\Sample",
            IsFramework: false,
            IsResourcePackage: false,
            "CN=Sample Publisher",
            "Store");
        var resolver = CreateResolver([], [package]);
        var metadata = CreateMetadata(CreatePackageRegistration(fullName));

        var result = resolver.Resolve(metadata);

        var owner = Assert.IsType<ApplicationOwnerMetadata>(result.Owner);
        Assert.Equal(ApplicationOwnerKind.Package, owner.Kind);
        Assert.Equal(OwnershipConfidence.Exact, owner.Confidence);
        Assert.Equal(fullName, owner.PackageFullName);
        Assert.Equal("Sample App", owner.DisplayName);
    }

    [Fact]
    public void ResolveUsesLongestInstallLocationAndPreservesUninstallEvidence()
    {
        var broad = CreateApplication("Suite", "C:\\Apps", "SuiteKey");
        var exact = CreateApplication("Feature", "C:\\Apps\\Feature", "FeatureKey") with
        {
            WindowsInstaller = true,
            ProductCode = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
            UninstallString = "msiexec.exe /x {AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
        };
        var resolver = CreateResolver([broad, exact], []);
        var metadata = CreateMetadata(
            CreateRegistryRegistration(),
            CreateBinary("C:\\Apps\\Feature\\ShellExtension.dll", "Feature Publisher"));

        var result = resolver.Resolve(metadata);

        var owner = Assert.IsType<ApplicationOwnerMetadata>(result.Owner);
        Assert.Equal("Feature", owner.DisplayName);
        Assert.Equal(OwnershipConfidence.High, owner.Confidence);
        Assert.True(owner.IsWindowsInstaller);
        Assert.Equal(exact.ProductCode, owner.ProductCode);
        Assert.Equal(exact.UninstallString, owner.UninstallString);
    }

    [Fact]
    public void ResolveDoesNotTreatSiblingPathAsInstallLocationChild()
    {
        var application = CreateApplication("Sample", "C:\\Apps\\Sample", "SampleKey");
        var resolver = CreateResolver([application], []);
        var metadata = CreateMetadata(
            CreateRegistryRegistration(),
            CreateBinary("C:\\Apps\\Sample2\\ShellExtension.dll", companyName: null));

        var result = resolver.Resolve(metadata);

        var owner = Assert.IsType<ApplicationOwnerMetadata>(result.Owner);
        Assert.Equal(ApplicationOwnerKind.Unknown, owner.Kind);
        Assert.Equal(OwnershipConfidence.None, owner.Confidence);
    }

    [Fact]
    public void ResolveProtectsHandlersInsideWindowsDirectory()
    {
        var resolver = CreateResolver([], []);
        var binaryPath = Path.Combine(Environment.SystemDirectory, "ShellExtension.dll");
        var metadata = CreateMetadata(
            CreateRegistryRegistration(),
            CreateBinary(binaryPath, "Microsoft Corporation"));

        var result = resolver.Resolve(metadata);

        var owner = Assert.IsType<ApplicationOwnerMetadata>(result.Owner);
        Assert.Equal(ApplicationOwnerKind.WindowsSystem, owner.Kind);
        Assert.Equal(OwnershipConfidence.High, owner.Confidence);
        Assert.True(owner.IsSystemProtected);
    }

    [Fact]
    public void ResolveLabelsPublisherOnlyMatchAsLowConfidence()
    {
        var application = CreateApplication("Publisher Match", installLocation: null, "PublisherKey");
        var resolver = CreateResolver([application], []);
        var metadata = CreateMetadata(
            CreateRegistryRegistration(),
            CreateBinary("D:\\Unmapped\\ShellExtension.dll", "Sample Publisher"));

        var result = resolver.Resolve(metadata);

        var owner = Assert.IsType<ApplicationOwnerMetadata>(result.Owner);
        Assert.Equal("Publisher Match", owner.DisplayName);
        Assert.Equal(OwnershipConfidence.Low, owner.Confidence);
    }

    private static ApplicationOwnershipResolver CreateResolver(
        IReadOnlyList<InstalledApplicationInfo> applications,
        IReadOnlyList<InstalledPackageInfo> packages) =>
        new(
            new FakeApplicationCatalog(applications),
            new FakePackageCatalog(packages));

    private static InstalledApplicationInfo CreateApplication(
        string displayName,
        string? installLocation,
        string keyName) => new(
            keyName,
            displayName,
            "Sample Publisher",
            "1.0.0",
            installLocation,
            DisplayIcon: null,
            UninstallString: "uninstall.exe",
            QuietUninstallString: null,
            ProductCode: null,
            WindowsInstaller: false,
            SystemComponent: false,
            NoRemove: false,
            new RegistrySource(
                RegistryHiveKind.LocalMachine,
                RegistryViewKind.Registry64,
                $"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{keyName}"));

    private static ContextMenuRegistrationMetadata CreateMetadata(
        ContextMenuRegistration registration,
        BinaryFileMetadata? binary = null)
    {
        IReadOnlyList<HandlerComponentMetadata> components = binary is null
            ? []
            :
            [
                new HandlerComponentMetadata(
                    HandlerComponentRole.ContextMenuHandler,
                    "{11111111-2222-3333-4444-555555555555}",
                    ComServer: null,
                    binary,
                    Issues: []),
            ];
        return new ContextMenuRegistrationMetadata(
            registration,
            components,
            Owner: null,
            Issues: []);
    }

    private static BinaryFileMetadata CreateBinary(string path, string? companyName) => new(
        path,
        Exists: true,
        Size: 1,
        DateTimeOffset.UnixEpoch,
        Sha256: null,
        BinaryArchitectureKind.X64,
        IsManaged: false,
        FileVersion: "1.0.0.0",
        ProductVersion: "1.0.0.0",
        ProductName: null,
        Description: null,
        companyName,
        new AuthenticodeSignatureMetadata(
            SignatureVerificationStatus.Unknown,
            PublisherName: null,
            Subject: null,
            Issuer: null,
            Thumbprint: null,
            ValidFrom: null,
            ValidTo: null,
            TrustErrorCode: null),
        Issues: []);

    private static ContextMenuRegistration CreateRegistryRegistration() => new()
    {
        Id = "registry",
        Source = new RegistryContextMenuSource(new RegistrySource(
            RegistryHiveKind.LocalMachine,
            RegistryViewKind.Registry64,
            "Software\\Classes\\*\\shellex\\ContextMenuHandlers\\Test")),
        ClassPath = "*",
        RegistrationPath = "Software\\Classes\\*\\shellex\\ContextMenuHandlers\\Test",
        CanonicalName = "Test",
        DisplayName = "Test",
        TargetKind = ContextMenuTargetKind.File,
        Kind = ContextMenuRegistrationKind.ClassicContextMenuHandler,
    };

    private static ContextMenuRegistration CreatePackageRegistration(string fullName) => new()
    {
        Id = "package",
        Source = new PackageContextMenuSource(
            "Sample.Package",
            fullName,
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
    };

    private sealed class FakeApplicationCatalog(IReadOnlyList<InstalledApplicationInfo> applications)
        : IInstalledApplicationCatalog
    {
        public InstalledApplicationCatalogResult GetApplications() => new(applications, []);
    }

    private sealed class FakePackageCatalog(IReadOnlyList<InstalledPackageInfo> packages)
        : IInstalledPackageCatalog
    {
        public InstalledPackageCatalogResult GetPackages() => new(packages, []);
    }
}
