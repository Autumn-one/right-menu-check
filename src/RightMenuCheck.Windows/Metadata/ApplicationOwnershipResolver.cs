using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Packages;

namespace RightMenuCheck.Windows.Metadata;

public sealed class ApplicationOwnershipResolver
{
    private readonly InstalledApplicationCatalogResult _applicationCatalog;
    private readonly InstalledPackageCatalogResult _packageCatalog;

    public ApplicationOwnershipResolver(
        IInstalledApplicationCatalog applicationCatalog,
        IInstalledPackageCatalog packageCatalog)
    {
        ArgumentNullException.ThrowIfNull(applicationCatalog);
        ArgumentNullException.ThrowIfNull(packageCatalog);
        _applicationCatalog = applicationCatalog.GetApplications();
        _packageCatalog = packageCatalog.GetPackages();

        CatalogIssues = _applicationCatalog.Issues
            .Concat(_packageCatalog.Issues.Select(static issue => new MetadataIssue(
                issue.PackageName,
                issue.Operation,
                issue.ErrorType,
                issue.Message)))
            .ToArray();
    }

    public IReadOnlyList<MetadataIssue> CatalogIssues { get; }

    public ContextMenuRegistrationMetadata Resolve(ContextMenuRegistrationMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var issues = metadata.Issues.ToList();
        var owner = metadata.Registration.Source is PackageContextMenuSource packageSource
            ? ResolvePackageOwner(packageSource)
            : ResolveRegistryOwner(metadata, issues);

        return metadata with
        {
            Owner = owner,
            Issues = issues,
        };
    }

    private ApplicationOwnerMetadata ResolvePackageOwner(PackageContextMenuSource source)
    {
        var package = _packageCatalog.Packages.FirstOrDefault(item =>
            item.FullName.Equals(source.PackageFullName, StringComparison.OrdinalIgnoreCase));
        if (package is null)
        {
            return new ApplicationOwnerMetadata(
                ApplicationOwnerKind.Package,
                OwnershipConfidence.Exact,
                source.PackageName,
                Publisher: null,
                Version: null,
                Path.GetDirectoryName(source.ManifestPath),
                ProductCode: null,
                source.PackageFullName,
                UninstallRegistrySource: null,
                UninstallKeyName: null,
                UninstallString: null,
                QuietUninstallString: null,
                IsWindowsInstaller: false,
                IsSystemProtected: false,
                "The package manifest directly identifies the owning PackageFullName; package metadata is unavailable.");
        }

        var isSystemProtected = IsMicrosoftPublisher(package.Publisher) ||
                                package.SignatureKind.Equals(
                                    "System",
                                    StringComparison.OrdinalIgnoreCase);
        return new ApplicationOwnerMetadata(
            ApplicationOwnerKind.Package,
            OwnershipConfidence.Exact,
            string.IsNullOrWhiteSpace(package.DisplayName) ? package.Name : package.DisplayName,
            string.IsNullOrWhiteSpace(package.PublisherDisplayName)
                ? package.Publisher
                : package.PublisherDisplayName,
            package.Version,
            package.InstallLocation,
            ProductCode: null,
            package.FullName,
            UninstallRegistrySource: null,
            UninstallKeyName: null,
            UninstallString: null,
            QuietUninstallString: null,
            IsWindowsInstaller: false,
            isSystemProtected,
            "The manifest PackageFullName exactly matches the installed package catalog.");
    }

    private ApplicationOwnerMetadata ResolveRegistryOwner(
        ContextMenuRegistrationMetadata metadata,
        List<MetadataIssue> issues)
    {
        var candidatePaths = GetCandidatePaths(metadata, issues);
        var windowsDirectory = TryNormalizePath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            metadata.Registration.Id,
            issues);

        if (windowsDirectory is not null && candidatePaths.Any(path =>
                IsPathWithin(path, windowsDirectory)))
        {
            return new ApplicationOwnerMetadata(
                ApplicationOwnerKind.WindowsSystem,
                OwnershipConfidence.High,
                "Microsoft Windows",
                "Microsoft Corporation",
                Environment.OSVersion.Version.ToString(),
                windowsDirectory,
                ProductCode: null,
                PackageFullName: null,
                UninstallRegistrySource: null,
                UninstallKeyName: null,
                UninstallString: null,
                QuietUninstallString: null,
                IsWindowsInstaller: false,
                IsSystemProtected: true,
                "The resolved handler binary is inside the Windows system directory tree.");
        }

        var installLocationMatches = _applicationCatalog.Applications
            .Select(application => new
            {
                Application = application,
                InstallLocation = TryNormalizePath(
                    application.InstallLocation,
                    application.KeyName,
                    issues),
            })
            .Where(item => item.InstallLocation is not null && candidatePaths.Any(path =>
                IsPathWithin(path, item.InstallLocation)))
            .OrderByDescending(item => item.InstallLocation!.Length)
            .ThenByDescending(item =>
                item.Application.Source.Hive == RegistryHiveKind.CurrentUser)
            .ThenBy(item => item.Application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();

        if (installLocationMatches is not null)
        {
            return CreateInstalledApplicationOwner(
                installLocationMatches.Application,
                OwnershipConfidence.High,
                "The resolved handler path is within the application's InstallLocation.");
        }

        var companies = metadata.Components
            .Select(component => component.Binary?.CompanyName)
            .Where(static company => !string.IsNullOrWhiteSpace(company))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var publisherMatches = _applicationCatalog.Applications
            .Where(application => application.Publisher is not null && companies.Contains(
                application.Publisher,
                StringComparer.OrdinalIgnoreCase))
            .GroupBy(static application => application.KeyName, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(2)
            .ToArray();

        if (publisherMatches.Length == 1)
        {
            return CreateInstalledApplicationOwner(
                publisherMatches[0],
                OwnershipConfidence.Low,
                "The binary CompanyName uniquely matches one uninstall publisher; no path ownership was proven.");
        }

        return new ApplicationOwnerMetadata(
            ApplicationOwnerKind.Unknown,
            OwnershipConfidence.None,
            metadata.Registration.DisplayName,
            Publisher: companies.Length == 1 ? companies[0] : null,
            Version: null,
            InstallLocation: null,
            ProductCode: null,
            PackageFullName: null,
            UninstallRegistrySource: null,
            UninstallKeyName: null,
            UninstallString: null,
            QuietUninstallString: null,
            IsWindowsInstaller: false,
            IsSystemProtected: false,
            "No installed application path, exact package identity, or unique publisher match was found.");
    }

    private static ApplicationOwnerMetadata CreateInstalledApplicationOwner(
        InstalledApplicationInfo application,
        OwnershipConfidence confidence,
        string matchReason)
    {
        var isSystemProtected = application.SystemComponent ||
                                application.NoRemove ||
                                IsMicrosoftPublisher(application.Publisher);
        return new ApplicationOwnerMetadata(
            ApplicationOwnerKind.InstalledApplication,
            confidence,
            application.DisplayName,
            application.Publisher,
            application.DisplayVersion,
            application.InstallLocation,
            application.ProductCode,
            PackageFullName: null,
            application.Source,
            application.KeyName,
            application.UninstallString,
            application.QuietUninstallString,
            application.WindowsInstaller,
            isSystemProtected,
            matchReason);
    }

    private static string[] GetCandidatePaths(
        ContextMenuRegistrationMetadata metadata,
        List<MetadataIssue> issues)
    {
        var paths = metadata.Components
            .Select(component => component.Binary?.Path ?? component.ComServer?.ResolvedServerPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => TryNormalizePath(path, metadata.Registration.Id, issues))
            .Where(static path => path is not null)
            .Cast<string>()
            .ToList();

        if (!string.IsNullOrWhiteSpace(metadata.Registration.Command))
        {
            try
            {
                var commandPath = CommandLineParser.TryGetExecutable(metadata.Registration.Command);
                if (commandPath is not null && Path.IsPathFullyQualified(commandPath) &&
                    TryNormalizePath(commandPath, metadata.Registration.Id, issues) is { } normalized)
                {
                    paths.Add(normalized);
                }
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                issues.Add(new MetadataIssue(
                    metadata.Registration.Id,
                    "ParseStaticVerbCommand",
                    exception.GetType().Name,
                    exception.Message));
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? TryNormalizePath(
        string? path,
        string component,
        List<MetadataIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
        }
        catch (ArgumentException exception)
        {
            AddIssue(exception);
        }
        catch (NotSupportedException exception)
        {
            AddIssue(exception);
        }
        catch (PathTooLongException exception)
        {
            AddIssue(exception);
        }

        return null;

        void AddIssue(Exception exception)
        {
            issues.Add(new MetadataIssue(
                component,
                "NormalizeOwnershipPath",
                exception.GetType().Name,
                exception.Message));
        }
    }

    private static bool IsPathWithin(string candidatePath, string rootPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        return !Path.IsPathRooted(relativePath) &&
               !relativePath.Equals("..", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsMicrosoftPublisher(string? publisher) =>
        publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
}
