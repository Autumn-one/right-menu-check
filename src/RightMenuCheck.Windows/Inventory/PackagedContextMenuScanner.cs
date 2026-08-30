using System.Diagnostics;
using System.Security;
using System.Xml;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Packages;

namespace RightMenuCheck.Windows.Inventory;

public sealed class PackagedContextMenuScanner
{
    private readonly IInstalledPackageCatalog _packageCatalog;
    private readonly IManifestStreamProvider _manifestStreamProvider;

    public PackagedContextMenuScanner(
        IInstalledPackageCatalog packageCatalog,
        IManifestStreamProvider manifestStreamProvider)
    {
        _packageCatalog = packageCatalog ?? throw new ArgumentNullException(nameof(packageCatalog));
        _manifestStreamProvider = manifestStreamProvider ??
                                  throw new ArgumentNullException(nameof(manifestStreamProvider));
    }

    public PackagedContextMenuScanResult Scan(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var registrations = new List<ContextMenuRegistration>();
        var issues = new List<PackageScanIssue>();
        var catalogResult = _packageCatalog.GetPackages();

        issues.AddRange(catalogResult.Issues.Select(static issue => new PackageScanIssue(
            issue.PackageName,
            string.Empty,
            issue.Operation,
            issue.ErrorType,
            issue.Message)));

        foreach (var package in catalogResult.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (package.IsFramework || package.IsResourcePackage)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(package.InstallLocation))
            {
                issues.Add(new PackageScanIssue(
                    package.FullName,
                    string.Empty,
                    "ResolveManifestPath",
                    "MissingInstallLocation",
                    "Package install location is unavailable."));
                continue;
            }

            var manifestPath = Path.Combine(package.InstallLocation, "AppxManifest.xml");
            try
            {
                using var manifestStream = _manifestStreamProvider.OpenRead(manifestPath);
                var parseResult = PackageManifestParser.Parse(manifestStream);

                foreach (var parseIssue in parseResult.Issues)
                {
                    issues.Add(new PackageScanIssue(
                        package.FullName,
                        manifestPath,
                        "ParseManifest",
                        "ManifestValidation",
                        FormatParseIssue(parseIssue)));
                }

                foreach (var verb in parseResult.Verbs)
                {
                    registrations.Add(CreateRegistration(package, manifestPath, verb));
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                AddReadIssue(exception);
            }
            catch (SecurityException exception)
            {
                AddReadIssue(exception);
            }
            catch (FileNotFoundException exception)
            {
                AddReadIssue(exception);
            }
            catch (DirectoryNotFoundException exception)
            {
                AddReadIssue(exception);
            }
            catch (IOException exception)
            {
                AddReadIssue(exception);
            }
            catch (XmlException exception)
            {
                AddReadIssue(exception);
            }

            void AddReadIssue(Exception exception)
            {
                issues.Add(new PackageScanIssue(
                    package.FullName,
                    manifestPath,
                    "ReadManifest",
                    exception.GetType().Name,
                    exception.Message));
            }
        }

        stopwatch.Stop();
        var ordered = registrations
            .OrderBy(static item =>
                ((PackageContextMenuSource)item.Source).PackageName,
                StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static item => item.ClassPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.HandlerClsid, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PackagedContextMenuScanResult(
            startedAt,
            stopwatch.Elapsed,
            ordered,
            issues);
    }

    private static ContextMenuRegistration CreateRegistration(
        InstalledPackageInfo package,
        string manifestPath,
        PackageManifestVerb verb)
    {
        var registrationPath =
            $"Applications\\{verb.ApplicationId}\\Extensions\\windows.fileExplorerContextMenus" +
            $"\\{verb.ItemType}\\{verb.VerbId}";

        return new ContextMenuRegistration
        {
            Id = $"{package.FullName}|{verb.ApplicationId}|{verb.ItemType}|{verb.VerbId}|{verb.HandlerClsid}",
            Source = new PackageContextMenuSource(
                package.Name,
                package.FullName,
                package.FamilyName,
                verb.ApplicationId,
                package.Architecture,
                manifestPath),
            ClassPath = verb.ItemType,
            RegistrationPath = registrationPath,
            CanonicalName = verb.VerbId,
            DisplayName = verb.VerbId,
            TargetKind = ContextMenuClassClassifier.Classify(verb.ItemType),
            Kind = ContextMenuRegistrationKind.PackagedExplorerCommand,
            HandlerClsid = verb.HandlerClsid,
        };
    }

    private static string FormatParseIssue(PackageManifestParseIssue issue) =>
        issue.LineNumber is { } lineNumber
            ? $"Line {lineNumber}: {issue.Message}"
            : issue.Message;
}
