using System.Text;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Inventory;
using RightMenuCheck.Windows.Packages;
using RightMenuCheck.Windows.Tests.Packages;

namespace RightMenuCheck.Windows.Tests.Inventory;

public sealed class PackagedContextMenuScannerTests
{
    [Fact]
    public void ScanCreatesPackageSourcesAndClassifiesTargets()
    {
        var package = CreatePackage("Sample.Package", "C:\\Packages\\Sample");
        var catalog = new FakePackageCatalog(package);
        var streams = new FakeManifestStreamProvider();
        streams.Add(
            "C:\\Packages\\Sample\\AppxManifest.xml",
            PackageManifestParserTests.ValidManifest);
        var scanner = new PackagedContextMenuScanner(catalog, streams);

        var result = scanner.Scan();

        Assert.Equal(2, result.Registrations.Count);
        var file = Assert.Single(
            result.Registrations,
            item => item.TargetKind == ContextMenuTargetKind.File);
        var background = Assert.Single(
            result.Registrations,
            item => item.TargetKind == ContextMenuTargetKind.FolderBackground);
        var source = Assert.IsType<PackageContextMenuSource>(file.Source);
        Assert.Equal("Sample.Package", source.PackageName);
        Assert.Equal(PackageArchitectureKind.X64, source.Architecture);
        Assert.Equal(ContextMenuRegistrationKind.PackagedExplorerCommand, file.Kind);
        Assert.Equal("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}", background.HandlerClsid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ScanSkipsFrameworkAndResourcePackages()
    {
        var framework = CreatePackage("Framework", "C:\\Packages\\Framework") with
        {
            IsFramework = true,
        };
        var resource = CreatePackage("Resource", "C:\\Packages\\Resource") with
        {
            IsResourcePackage = true,
        };
        var streams = new FakeManifestStreamProvider();
        var scanner = new PackagedContextMenuScanner(
            new FakePackageCatalog(framework, resource),
            streams);

        var result = scanner.Scan();

        Assert.Empty(result.Registrations);
        Assert.Empty(result.Issues);
        Assert.Equal(0, streams.OpenCount);
    }

    [Fact]
    public void ScanRecordsUnreadableManifestAndContinuesWithOtherPackages()
    {
        var missing = CreatePackage("Missing", "C:\\Packages\\Missing");
        var valid = CreatePackage("Valid", "C:\\Packages\\Valid");
        var streams = new FakeManifestStreamProvider();
        streams.Add(
            "C:\\Packages\\Valid\\AppxManifest.xml",
            PackageManifestParserTests.ValidManifest);
        var scanner = new PackagedContextMenuScanner(
            new FakePackageCatalog(missing, valid),
            streams);

        var result = scanner.Scan();

        Assert.Equal(2, result.Registrations.Count);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(missing.FullName, issue.PackageFullName);
        Assert.Equal("ReadManifest", issue.Operation);
        Assert.Equal(nameof(FileNotFoundException), issue.ErrorType);
    }

    [Fact]
    public void ScanReportsMissingInstallLocationForApplicationPackage()
    {
        var package = CreatePackage("MissingLocation", string.Empty);
        var scanner = new PackagedContextMenuScanner(
            new FakePackageCatalog(package),
            new FakeManifestStreamProvider());

        var result = scanner.Scan();

        var issue = Assert.Single(result.Issues);
        Assert.Equal("ResolveManifestPath", issue.Operation);
        Assert.Equal("MissingInstallLocation", issue.ErrorType);
    }

    private static InstalledPackageInfo CreatePackage(string name, string installLocation) => new(
        name,
        $"{name}_1.0.0.0_x64__publisher",
        $"{name}_publisher",
        name,
        "Publisher",
        "1.0.0.0",
        PackageArchitectureKind.X64,
        installLocation,
        IsFramework: false,
        IsResourcePackage: false);

    private sealed class FakePackageCatalog(params InstalledPackageInfo[] packages)
        : IInstalledPackageCatalog
    {
        public InstalledPackageCatalogResult GetPackages() => new(packages, []);
    }

    private sealed class FakeManifestStreamProvider : IManifestStreamProvider
    {
        private readonly Dictionary<string, string> _manifests =
            new(StringComparer.OrdinalIgnoreCase);

        public int OpenCount { get; private set; }

        public void Add(string path, string content) => _manifests.Add(path, content);

        public Stream OpenRead(string manifestPath)
        {
            OpenCount++;
            if (!_manifests.TryGetValue(manifestPath, out var content))
            {
                throw new FileNotFoundException("Manifest fixture was not registered.", manifestPath);
            }

            return new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);
        }
    }
}
