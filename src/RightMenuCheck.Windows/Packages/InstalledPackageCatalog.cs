using System.Runtime.InteropServices;
using RightMenuCheck.Core.Inventory;
using Windows.Management.Deployment;

namespace RightMenuCheck.Windows.Packages;

public sealed record InstalledPackageInfo(
    string Name,
    string FullName,
    string FamilyName,
    string DisplayName,
    string PublisherDisplayName,
    string Version,
    PackageArchitectureKind Architecture,
    string InstallLocation,
    bool IsFramework,
    bool IsResourcePackage,
    string Publisher = "",
    string SignatureKind = "Unknown");

public sealed record PackageCatalogIssue(
    string PackageName,
    string Operation,
    string ErrorType,
    string Message);

public sealed record InstalledPackageCatalogResult(
    IReadOnlyList<InstalledPackageInfo> Packages,
    IReadOnlyList<PackageCatalogIssue> Issues);

public interface IInstalledPackageCatalog
{
    InstalledPackageCatalogResult GetPackages();
}

public sealed class SystemInstalledPackageCatalog : IInstalledPackageCatalog
{
    public InstalledPackageCatalogResult GetPackages()
    {
        var packages = new List<InstalledPackageInfo>();
        var issues = new List<PackageCatalogIssue>();
        global::Windows.ApplicationModel.Package[] nativePackages;

        try
        {
            var packageManager = new PackageManager();
            nativePackages = packageManager.FindPackagesForUser(string.Empty).ToArray();
        }
        catch (UnauthorizedAccessException exception)
        {
            AddCatalogIssue(exception);
            return new InstalledPackageCatalogResult(packages, issues);
        }
        catch (COMException exception)
        {
            AddCatalogIssue(exception);
            return new InstalledPackageCatalogResult(packages, issues);
        }

        foreach (var package in nativePackages)
        {
            var packageName = string.Empty;
            try
            {
                var id = package.Id;
                packageName = id.Name;
                var version = id.Version;
                packages.Add(new InstalledPackageInfo(
                    packageName,
                    id.FullName,
                    id.FamilyName,
                    package.DisplayName,
                    package.PublisherDisplayName,
                    $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                    MapArchitecture(id.Architecture.ToString()),
                    package.InstalledLocation.Path,
                    package.IsFramework,
                    package.IsResourcePackage,
                    id.Publisher,
                    package.SignatureKind.ToString()));
            }
            catch (UnauthorizedAccessException exception)
            {
                AddPackageIssue(packageName, exception);
            }
            catch (FileNotFoundException exception)
            {
                AddPackageIssue(packageName, exception);
            }
            catch (COMException exception)
            {
                AddPackageIssue(packageName, exception);
            }
        }

        return new InstalledPackageCatalogResult(packages, issues);

        void AddCatalogIssue(Exception exception) =>
            issues.Add(new PackageCatalogIssue(
                string.Empty,
                "FindPackagesForUser",
                exception.GetType().Name,
                exception.Message));

        void AddPackageIssue(string packageName, Exception exception) =>
            issues.Add(new PackageCatalogIssue(
                packageName,
                "ReadPackageMetadata",
                exception.GetType().Name,
                exception.Message));
    }

    private static PackageArchitectureKind MapArchitecture(string architecture) =>
        architecture.ToUpperInvariant() switch
        {
            "NEUTRAL" => PackageArchitectureKind.Neutral,
            "X86" => PackageArchitectureKind.X86,
            "X64" => PackageArchitectureKind.X64,
            "ARM" => PackageArchitectureKind.Arm,
            "ARM64" => PackageArchitectureKind.Arm64,
            "X86ONARM64" => PackageArchitectureKind.X86OnArm64,
            _ => PackageArchitectureKind.Unknown,
        };
}
