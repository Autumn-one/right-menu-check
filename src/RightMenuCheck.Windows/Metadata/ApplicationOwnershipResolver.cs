using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Packages;

namespace RightMenuCheck.Windows.Metadata;

public sealed class ApplicationOwnershipResolver
{
    private readonly InstalledApplicationCatalogResult _applicationCatalog;
    private readonly InstalledPackageCatalogResult _packageCatalog;
    private readonly ApplicationPathMatch[] _applicationPathMatches;

    public ApplicationOwnershipResolver(
        IInstalledApplicationCatalog applicationCatalog,
        IInstalledPackageCatalog packageCatalog)
    {
        ArgumentNullException.ThrowIfNull(applicationCatalog);
        ArgumentNullException.ThrowIfNull(packageCatalog);
        _applicationCatalog = applicationCatalog.GetApplications();
        _packageCatalog = packageCatalog.GetPackages();

        var catalogIssues = _applicationCatalog.Issues
            .Concat(_packageCatalog.Issues.Select(static issue => new MetadataIssue(
                issue.PackageName,
                issue.Operation,
                issue.ErrorType,
                issue.Message)))
            .ToList();
        _applicationPathMatches = _applicationCatalog.Applications
            .SelectMany(application => GetApplicationPathEvidence(application, catalogIssues)
                .Select(evidence => new ApplicationPathMatch(
                    application,
                    evidence.RootPath,
                    evidence.Kind)))
            .ToArray();
        CatalogIssues = catalogIssues.ToArray();
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
                "清单已精确给出 PackageFullName，但系统未返回该包的更多元数据。");
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
            "清单中的 PackageFullName 与系统已安装包目录精确匹配。");
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
                "处理程序二进制文件位于 Windows 系统目录中。");
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
                "处理程序路径位于该应用声明的 InstallLocation 中。");
        }

        var supportingPathMatch = FindSupportingPathMatch(candidatePaths);
        if (supportingPathMatch is not null)
        {
            var reason = supportingPathMatch.Kind == ApplicationPathEvidenceKind.DisplayIcon
                ? "处理程序路径与该卸载项的 DisplayIcon 位于同一应用目录中。"
                : "处理程序路径与该卸载项的卸载程序位于同一应用目录中。";
            return CreateInstalledApplicationOwner(
                supportingPathMatch.Application,
                OwnershipConfidence.High,
                reason);
        }

        var companies = metadata.Components
            .Select(static component => component.Binary?.CompanyName)
            .Where(static company => !string.IsNullOrWhiteSpace(company))
            .Cast<string>()
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
                "二进制发布者只匹配到一个卸载项，但尚无路径证据，因此仅作线索。");
        }

        var publishers = metadata.Components
            .SelectMany(static component => new[]
            {
                component.Binary?.Signature.PublisherName,
                component.Binary?.CompanyName,
            })
            .Where(static publisher => !string.IsNullOrWhiteSpace(publisher))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var productNames = metadata.Components
            .Select(static component => component.Binary?.ProductName)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var descriptions = metadata.Components
            .Select(static component => component.Binary?.Description)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var binaryIdentity = productNames.Length == 1
            ? productNames[0]
            : descriptions.Length == 1
                ? descriptions[0]
                : null;

        return new ApplicationOwnerMetadata(
            ApplicationOwnerKind.Unknown,
            binaryIdentity is null ? OwnershipConfidence.None : OwnershipConfidence.Low,
            binaryIdentity ?? metadata.Registration.DisplayName,
            Publisher: publishers.Length == 1 ? publishers[0] : null,
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
            binaryIdentity is null
                ? "未找到安装目录、包身份或唯一发布者匹配，无法确认所属应用。"
                : "名称仅来自处理程序文件的产品信息，尚未匹配到 Windows 已安装应用，不能据此卸载。");
    }

    private ApplicationPathMatch? FindSupportingPathMatch(IReadOnlyList<string> candidatePaths)
    {
        var matches = _applicationPathMatches
            .Where(match => candidatePaths.Any(path => IsPathWithin(path, match.RootPath)))
            .OrderByDescending(static match => match.RootPath.Length)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        var longestPathLength = matches[0].RootPath.Length;
        // Supporting paths are high confidence only when the most specific root has one owner.
        var mostSpecificMatches = matches
            .Where(match => match.RootPath.Length == longestPathLength)
            .GroupBy(static match => match.Application.KeyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mostSpecificMatches.Length != 1)
        {
            return null;
        }

        return mostSpecificMatches[0]
            .OrderBy(static match => match.Kind)
            .ThenByDescending(static match =>
                match.Application.Source.Hive == RegistryHiveKind.CurrentUser)
            .ThenBy(static match => match.Application.Source.View)
            .First();
    }

    private static ApplicationPathEvidence[] GetApplicationPathEvidence(
        InstalledApplicationInfo application,
        List<MetadataIssue> issues)
    {
        var evidence = new List<ApplicationPathEvidence>();
        AddEvidence(
            TryGetDisplayIconPath(application.DisplayIcon),
            ApplicationPathEvidenceKind.DisplayIcon);
        AddEvidence(
            TryGetUninstallExecutable(application.UninstallString, application, issues),
            ApplicationPathEvidenceKind.UninstallExecutable);
        AddEvidence(
            TryGetUninstallExecutable(application.QuietUninstallString, application, issues),
            ApplicationPathEvidenceKind.UninstallExecutable);
        return evidence
            .DistinctBy(static item => (item.RootPath.ToUpperInvariant(), item.Kind))
            .ToArray();

        void AddEvidence(string? executablePath, ApplicationPathEvidenceKind kind)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            string expandedExecutable;
            try
            {
                expandedExecutable = Environment.ExpandEnvironmentVariables(executablePath);
                if (!Path.IsPathFullyQualified(expandedExecutable))
                {
                    return;
                }
            }
            catch (ArgumentException exception)
            {
                issues.Add(new MetadataIssue(
                    application.KeyName,
                    "NormalizeOwnershipEvidencePath",
                    exception.GetType().Name,
                    exception.Message));
                return;
            }

            var normalizedExecutable = TryNormalizePath(
                expandedExecutable,
                application.KeyName,
                issues);
            var directory = normalizedExecutable is null
                ? null
                : Path.GetDirectoryName(normalizedExecutable);
            var normalizedDirectory = TryNormalizePath(
                directory,
                application.KeyName,
                issues);
            if (normalizedDirectory is not null && IsSpecificApplicationRoot(normalizedDirectory))
            {
                evidence.Add(new ApplicationPathEvidence(normalizedDirectory, kind));
            }
        }
    }

    private static string? TryGetDisplayIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var value = displayIcon.Trim().TrimStart('@');
        if (value.Length > 1 && value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote > 1)
            {
                return value[1..closingQuote];
            }
        }

        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(
                value.AsSpan(comma + 1),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            value = value[..comma];
        }

        return value.Trim().Trim('"');
    }

    private static string? TryGetUninstallExecutable(
        string? uninstallCommand,
        InstalledApplicationInfo application,
        List<MetadataIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(uninstallCommand))
        {
            return null;
        }

        try
        {
            var executable = CommandLineParser.TryGetExecutable(uninstallCommand);
            return executable is not null &&
                   Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? executable
                : null;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            issues.Add(new MetadataIssue(
                application.KeyName,
                "ParseOwnershipUninstallCommand",
                exception.GetType().Name,
                exception.Message));
            return null;
        }
        catch (ArgumentException exception)
        {
            issues.Add(new MetadataIssue(
                application.KeyName,
                "ParseOwnershipUninstallCommand",
                exception.GetType().Name,
                exception.Message));
            return null;
        }
    }

    private static bool IsSpecificApplicationRoot(string path)
    {
        var pathRoot = Path.GetPathRoot(path);
        if (pathRoot is null || path.Equals(
                Path.TrimEndingDirectorySeparator(pathRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var broadRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
        };
        return !broadRoots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.TrimEndingDirectorySeparator)
            .Contains(path, StringComparer.OrdinalIgnoreCase);
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

    private enum ApplicationPathEvidenceKind
    {
        DisplayIcon,
        UninstallExecutable,
    }

    private sealed record ApplicationPathEvidence(
        string RootPath,
        ApplicationPathEvidenceKind Kind);

    private sealed record ApplicationPathMatch(
        InstalledApplicationInfo Application,
        string RootPath,
        ApplicationPathEvidenceKind Kind);
}
