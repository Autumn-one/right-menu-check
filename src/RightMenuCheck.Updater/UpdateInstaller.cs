using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.Updater;

public sealed record UpdateInstallResult(
    bool Succeeded,
    bool RolledBack,
    string Message,
    string? ErrorType);

public sealed class UpdateInstaller
{
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(45);
    private readonly IUpdateHealthMonitor _healthMonitor;
    private readonly IAppLogger _logger;
    private readonly IUpdateProcessController _processController;
    private readonly string _publicKeyPem;
    private readonly ISafeZipExtractor _zipExtractor;

    public UpdateInstaller(
        ISafeZipExtractor zipExtractor,
        IUpdateProcessController processController,
        IUpdateHealthMonitor healthMonitor,
        string publicKeyPem,
        IAppLogger logger)
    {
        _zipExtractor = zipExtractor ?? throw new ArgumentNullException(nameof(zipExtractor));
        _processController = processController ??
                             throw new ArgumentNullException(nameof(processController));
        _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        _publicKeyPem = publicKeyPem;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (paths, expectedVersion) = ValidateAndCreatePaths(request, _publicKeyPem);
        _logger.Log(
            AppLogLevel.Information,
            "update.install_started",
            "Update installation started.",
            new Dictionary<string, object?>
            {
                ["expectedVersion"] = expectedVersion.ToString(),
                ["parentProcessId"] = request.ParentProcessId,
            });

        var oldInstallMoved = false;
        try
        {
            await VerifyPackageAsync(
                    paths.PackagePath,
                    request.Manifest.Payload.Package,
                    cancellationToken)
                .ConfigureAwait(false);
            await _processController.WaitForExitAsync(
                    request.ParentProcessId,
                    paths.CurrentApplicationPath,
                    ParentExitTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            await _zipExtractor.ExtractAsync(
                    paths.PackagePath,
                    paths.StagingDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateStagedPayload(paths, expectedVersion);
            TryDeleteFile(_healthMonitor.GetMarkerPath(request.HealthToken));

            Directory.Move(paths.InstallDirectory, paths.BackupDirectory);
            oldInstallMoved = true;
            Directory.Move(paths.StagingDirectory, paths.InstallDirectory);

            var updatedProcess = _processController.Start(
                paths.CurrentApplicationPath,
                paths.InstallDirectory,
                ["--update-health-token", request.HealthToken]);
            var healthy = await _healthMonitor.WaitForHealthyAsync(
                    request.HealthToken,
                    updatedProcess,
                    _processController,
                    HealthTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!healthy)
            {
                await _processController.StopAsync(updatedProcess, cancellationToken)
                    .ConfigureAwait(false);
                RollBack(paths);
                _ = _processController.Start(
                    paths.CurrentApplicationPath,
                    paths.InstallDirectory,
                    ["--update-rollback"]);
                return new UpdateInstallResult(
                    Succeeded: false,
                    RolledBack: true,
                    "The updated application did not report healthy startup; the previous version was restored.",
                    "HealthCheckFailed");
            }

            TryDeleteDirectory(paths.BackupDirectory);
            TryDeleteFile(paths.PackagePath);
            TryDeleteFile(_healthMonitor.GetMarkerPath(request.HealthToken));
            return new UpdateInstallResult(
                Succeeded: true,
                RolledBack: false,
                "Update installation completed.",
                ErrorType: null);
        }
        catch (Exception exception) when (exception is
                                           ArgumentException or
                                           InvalidDataException or
                                           IOException or
                                           UnauthorizedAccessException or
                                           TimeoutException or
                                           InvalidOperationException or
                                           Win32Exception or
                                           JsonException)
        {
            if (oldInstallMoved)
            {
                RollBack(paths);
                _ = _processController.Start(
                    paths.CurrentApplicationPath,
                    paths.InstallDirectory,
                    ["--update-rollback"]);
            }
            else
            {
                TryDeleteDirectory(paths.StagingDirectory);
            }

            return new UpdateInstallResult(
                Succeeded: false,
                RolledBack: oldInstallMoved,
                exception.Message,
                exception.GetType().Name);
        }
    }

    private static (UpdatePaths Paths, SemanticVersion ExpectedVersion) ValidateAndCreatePaths(
        UpdateInstallRequest request,
        string publicKeyPem)
    {
        if (request.SchemaVersion != UpdateInstallRequest.CurrentSchemaVersion ||
            request.ParentProcessId <= 0 ||
            !Guid.TryParseExact(request.HealthToken, "N", out _) ||
            !Path.IsPathFullyQualified(request.PackagePath) ||
            !Path.IsPathFullyQualified(request.InstallDirectory) ||
            string.IsNullOrWhiteSpace(request.ApplicationFileName) ||
            !Path.GetFileName(request.ApplicationFileName).Equals(
                request.ApplicationFileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update installation request is invalid.");
        }

        var manifestDecision = UpdatePolicyEvaluator.Evaluate(
            SemanticVersion.Parse("0.0.0"),
            request.Manifest,
            publicKeyPem);
        if (manifestDecision.Kind == UpdateDecisionKind.InvalidManifest ||
            manifestDecision.TargetVersion is not { } expectedVersion)
        {
            throw new InvalidDataException("Update installation manifest is invalid.");
        }

        var packagePath = Path.GetFullPath(request.PackagePath);
        var installDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.InstallDirectory));
        var parentDirectory = Directory.GetParent(installDirectory)?.FullName ??
                              throw new InvalidDataException("Install directory cannot be a root directory.");
        if (!File.Exists(packagePath) || !Directory.Exists(installDirectory))
        {
            throw new InvalidDataException("Update package or install directory does not exist.");
        }

        if (!Path.GetFileName(packagePath).Equals(
                request.Manifest.Payload.Package.AssetName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update package name does not match the signed manifest.");
        }

        var installName = Path.GetFileName(installDirectory);
        var stagingDirectory = Path.Combine(
            parentDirectory,
            $".{installName}.staging-{request.HealthToken}");
        var backupDirectory = Path.Combine(
            parentDirectory,
            $".{installName}.backup-{request.HealthToken}");
        var failedDirectory = Path.Combine(
            parentDirectory,
            $".{installName}.failed-{request.HealthToken}");
        if (Directory.Exists(stagingDirectory) ||
            Directory.Exists(backupDirectory) ||
            Directory.Exists(failedDirectory))
        {
            throw new InvalidDataException("Update working directories already exist.");
        }

        return (new UpdatePaths(
            packagePath,
            installDirectory,
            stagingDirectory,
            backupDirectory,
            failedDirectory,
            Path.Combine(installDirectory, request.ApplicationFileName),
            Path.Combine(stagingDirectory, request.ApplicationFileName),
            Path.Combine(stagingDirectory, "build-info.json")), expectedVersion);
    }

    private static async Task VerifyPackageAsync(
        string packagePath,
        UpdatePackage package,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(packagePath);
        if (file.Length != package.SizeBytes)
        {
            throw new InvalidDataException("Update package size does not match the signed manifest.");
        }

        await using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!Convert.ToHexString(hash).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update package hash does not match the signed manifest.");
        }
    }

    private static void ValidateStagedPayload(
        UpdatePaths paths,
        SemanticVersion expectedVersion)
    {
        if (!File.Exists(paths.StagedApplicationPath) || !File.Exists(paths.StagedBuildInfoPath))
        {
            throw new InvalidDataException("Update archive is missing required application files.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(paths.StagedBuildInfoPath));
        if (!document.RootElement.TryGetProperty("version", out var versionElement) ||
            !SemanticVersion.TryParse(versionElement.GetString(), out var packagedVersion) ||
            packagedVersion.CompareTo(expectedVersion) != 0)
        {
            throw new InvalidDataException("Update archive version does not match the manifest.");
        }
    }

    private static void RollBack(UpdatePaths paths)
    {
        if (Directory.Exists(paths.FailedDirectory))
        {
            throw new InvalidDataException("Rollback destination unexpectedly exists.");
        }

        if (Directory.Exists(paths.InstallDirectory))
        {
            Directory.Move(paths.InstallDirectory, paths.FailedDirectory);
        }

        if (!Directory.Exists(paths.BackupDirectory))
        {
            throw new InvalidDataException("Rollback backup is missing.");
        }

        Directory.Move(paths.BackupDirectory, paths.InstallDirectory);
        TryDeleteDirectory(paths.FailedDirectory);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record UpdatePaths(
        string PackagePath,
        string InstallDirectory,
        string StagingDirectory,
        string BackupDirectory,
        string FailedDirectory,
        string CurrentApplicationPath,
        string StagedApplicationPath,
        string StagedBuildInfoPath);
}
