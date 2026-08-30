using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Inventory;
using RightMenuCheck.Windows.Metadata;
using RightMenuCheck.Windows.Packages;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.IntegrationTests.Metadata;

public sealed class SystemComServerResolverTests
{
    [Fact]
    public void ResolveFindsExistingClassicComServerBinary()
    {
        var registryReader = new SystemRegistryReader();
        var inventory = new ContextMenuRegistryScanner(registryReader).Scan();
        var resolver = new ComServerResolver(registryReader);

        var resolvedBinary = inventory.Registrations
            .Where(item => item.HandlerClsid is not null)
            .SelectMany(resolver.Resolve)
            .Select(item => item.ComServer?.ResolvedServerPath)
            .FirstOrDefault(path => path is not null && File.Exists(path));

        Assert.NotNull(resolvedBinary);
    }

    [Fact]
    public void ResolveCrossReferencesPackageManifestWithPackagedComRegistry()
    {
        var registryReader = new SystemRegistryReader();
        var inventory = new PackagedContextMenuScanner(
            new SystemInstalledPackageCatalog(),
            new PhysicalManifestStreamProvider()).Scan();
        var resolver = new ComServerResolver(registryReader);

        var resolvedServer = inventory.Registrations
            .SelectMany(resolver.Resolve)
            .Select(item => item.ComServer)
            .FirstOrDefault(server =>
                server?.Kind == ComServerKind.PackagedInProcess &&
                server.ResolvedServerPath is not null &&
                File.Exists(server.ResolvedServerPath));

        Assert.NotNull(resolvedServer);
        Assert.NotNull(resolvedServer.PackageFullName);
        Assert.NotNull(resolvedServer.RegistrySource);
    }
}
