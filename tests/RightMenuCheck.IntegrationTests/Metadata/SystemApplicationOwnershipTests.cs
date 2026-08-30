using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Inventory;
using RightMenuCheck.Windows.Metadata;
using RightMenuCheck.Windows.Packages;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.IntegrationTests.Metadata;

public sealed class SystemApplicationOwnershipTests
{
    [Fact]
    public void CatalogReadsInstalledApplicationsAndMsiProductCodes()
    {
        var catalog = new RegistryInstalledApplicationCatalog(new SystemRegistryReader());

        var result = catalog.GetApplications();

        Assert.NotEmpty(result.Applications);
        Assert.All(result.Applications, application =>
        {
            Assert.False(string.IsNullOrWhiteSpace(application.DisplayName));
            Assert.StartsWith(
                "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\",
                application.Source.KeyPath,
                StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(
            result.Applications,
            application => application.WindowsInstaller && application.ProductCode is not null);
    }

    [Fact]
    public void ResolveAssignsExactOwnerToInstalledPackagedMenu()
    {
        var registryReader = new SystemRegistryReader();
        var packageCatalog = new SystemInstalledPackageCatalog();
        var packageRegistrations = new PackagedContextMenuScanner(
                packageCatalog,
                new PhysicalManifestStreamProvider())
            .Scan()
            .Registrations;
        var packageRegistration = packageRegistrations[0];
        var resolver = new ApplicationOwnershipResolver(
            new RegistryInstalledApplicationCatalog(registryReader),
            packageCatalog);
        var metadata = new ContextMenuRegistrationMetadata(
            packageRegistration,
            Components: [],
            Owner: null,
            Issues: []);

        var result = resolver.Resolve(metadata);

        var owner = Assert.IsType<ApplicationOwnerMetadata>(result.Owner);
        var source = Assert.IsType<PackageContextMenuSource>(packageRegistration.Source);
        Assert.Equal(ApplicationOwnerKind.Package, owner.Kind);
        Assert.Equal(OwnershipConfidence.Exact, owner.Confidence);
        Assert.Equal(source.PackageFullName, owner.PackageFullName);
    }
}
