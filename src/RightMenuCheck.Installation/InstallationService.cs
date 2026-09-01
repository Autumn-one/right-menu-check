using System.Security.Cryptography;
using System.Runtime.ExceptionServices;

namespace RightMenuCheck.Installation;

public sealed class InstallationService
{
    private const int BufferSize = 81920;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumUninstallerBytes = 256L * 1024 * 1024;
    private readonly IInstallIntegration _integration;

    public InstallationService(IInstallIntegration integration)
    {
        _integration = integration ?? throw new ArgumentNullException(nameof(integration));
    }

    public async Task<InstallationResult> InstallAsync(
        IInstallationPayloadSource payloadSource,
        InstallationPaths paths,
        InstallationOptions options,
        IProgress<InstallationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadSource);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        paths.Validate();
        var expectedVersion = payloadSource.ExpectedVersion;
        _ = RightMenuCheck.Distribution.SemanticVersion.Parse(expectedVersion);
        var expectedHash = NormalizeSha256(payloadSource.ExpectedPackageSha256);
        var installDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(paths.InstallDirectory));
        var installParent = Directory.GetParent(installDirectory)?.FullName ??
                            throw new InvalidDataException(
                                "Install directory cannot be a filesystem root.");
        Directory.CreateDirectory(installParent);
        SafeFileTree.EnsureDirectoryIsNotReparsePoint(installParent);
        SafeFileTree.EnsureDirectoryIsNotReparsePoint(paths.InstallerCacheDirectory);

        var transactionId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(
            installParent,
            $".RightMenuCheck.install-{transactionId}");
        var backupDirectory = Path.Combine(
            installParent,
            $".RightMenuCheck.backup-{transactionId}");
        var packagePath = Path.Combine(
            installParent,
            $".RightMenuCheck.package-{transactionId}.zip");
        var uninstallerNewPath = paths.UninstallerPath + $".new-{transactionId}";
        var uninstallerBackupPath = Path.Combine(
            installParent,
            $".RightMenuCheck.uninstaller-{transactionId}.bak");
        var oldInstallMoved = false;
        var newInstallActivated = false;
        var hadOldUninstaller = false;
        var integrationApplied = false;
        var installationCompleted = false;
        var rollbackCompleted = false;
        var uninstallerReplaced = false;
        IInstallIntegrationSnapshot? integrationSnapshot = null;
        try
        {
            Report(progress, 4, "正在验证安装资源…");
            await CopyAndVerifyPackageAsync(
                    payloadSource.OpenApplicationPackage(),
                    packagePath,
                    expectedHash,
                    cancellationToken)
                .ConfigureAwait(false);
            Report(progress, 14, "正在展开应用文件…");
            var extractionProgress = progress is null
                ? null
                : new Progress<double>(value =>
                    Report(progress, 14 + (value * 56), "正在展开应用文件…"));
            await SafeZipPayloadExtractor.ExtractAsync(
                    packagePath,
                    stagingDirectory,
                    extractionProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            var validated = PayloadValidator.Validate(stagingDirectory, expectedVersion);
            Report(progress, 74, "正在准备卸载组件…");
            Directory.CreateDirectory(paths.InstallerCacheDirectory);
            await CopyUninstallerAsync(
                    payloadSource.OpenUninstaller(),
                    uninstallerNewPath,
                    cancellationToken)
                .ConfigureAwait(false);
            await _integration.EnsureApplicationStoppedAsync(
                    paths.ApplicationPath,
                    cancellationToken)
                .ConfigureAwait(false);
            integrationSnapshot = _integration.Capture(paths);
            if (File.Exists(paths.UninstallerPath))
            {
                File.Copy(paths.UninstallerPath, uninstallerBackupPath, overwrite: false);
                hadOldUninstaller = true;
            }

            Report(progress, 82, "正在切换到新版本…");
            if (Directory.Exists(installDirectory))
            {
                SafeFileTree.EnsureDirectoryIsNotReparsePoint(installDirectory);
                Directory.Move(installDirectory, backupDirectory);
                oldInstallMoved = true;
            }

            Directory.Move(stagingDirectory, installDirectory);
            newInstallActivated = true;
            File.Move(uninstallerNewPath, paths.UninstallerPath, overwrite: true);
            uninstallerReplaced = true;
            Report(progress, 92, "正在创建快捷方式并注册卸载信息…");
            integrationApplied = true;
            _integration.Apply(
                paths,
                validated.Version,
                options.CreateDesktopShortcut,
                Math.Max(1, (validated.SizeBytes + 1023) / 1024));
            Report(progress, 98, "正在完成安装…");
            SafeFileTree.TryDeleteDirectory(backupDirectory);
            installationCompleted = true;
            Report(progress, 100, "安装完成。");
            return new InstallationResult(
                validated.Version,
                installDirectory,
                Path.Combine(
                    installDirectory,
                    RightMenuCheck.Distribution.UpdateInstallLocations.ApplicationFileName));
        }
        catch (Exception installationException)
        {
            var rollbackFailures = new List<Exception>();
            if (integrationSnapshot is not null && integrationApplied)
            {
                TryRollbackAction(
                    () => _integration.Restore(paths, integrationSnapshot),
                    rollbackFailures);
            }

            if (newInstallActivated)
            {
                TryRollbackAction(
                    () => SafeFileTree.DeleteDirectory(installDirectory),
                    rollbackFailures);
                newInstallActivated = Directory.Exists(installDirectory);
            }

            if (oldInstallMoved && Directory.Exists(backupDirectory))
            {
                if (Directory.Exists(installDirectory))
                {
                    rollbackFailures.Add(new IOException(
                        "The new installation could not be removed; the previous version remains in backup."));
                }
                else
                {
                    TryRollbackAction(
                        () => Directory.Move(backupDirectory, installDirectory),
                        rollbackFailures);
                    oldInstallMoved = Directory.Exists(backupDirectory);
                }
            }

            if (uninstallerReplaced)
            {
                TryRollbackAction(
                    () => RestoreUninstaller(
                        paths.UninstallerPath,
                        uninstallerBackupPath,
                        hadOldUninstaller),
                    rollbackFailures);
            }

            rollbackCompleted = rollbackFailures.Count == 0;
            if (!rollbackCompleted)
            {
                throw new AggregateException(
                    "Installation failed and rollback did not complete; recovery artifacts were preserved.",
                    [installationException, .. rollbackFailures]);
            }

            ExceptionDispatchInfo.Capture(installationException).Throw();
            throw;
        }
        finally
        {
            SafeFileTree.TryDeleteDirectory(stagingDirectory);
            SafeFileTree.TryDeleteFile(packagePath);
            SafeFileTree.TryDeleteFile(uninstallerNewPath);
            if (installationCompleted || rollbackCompleted)
            {
                SafeFileTree.TryDeleteDirectory(backupDirectory);
                SafeFileTree.TryDeleteFile(uninstallerBackupPath);
            }
        }
    }

    private static async Task CopyAndVerifyPackageAsync(
        Stream source,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using (source.ConfigureAwait(false))
        await using (var destination = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         BufferSize,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[BufferSize];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaximumPackageBytes)
                {
                    throw new InvalidDataException("Installer package is too large.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total == 0 || !Convert.ToHexString(hash.GetHashAndReset()).Equals(
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Installer package integrity check failed.");
            }
        }
    }

    private static async Task CopyUninstallerAsync(
        Stream source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using (source.ConfigureAwait(false))
        await using (var destination = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         BufferSize,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            var buffer = new byte[BufferSize];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > MaximumUninstallerBytes)
                {
                    throw new InvalidDataException("Embedded uninstaller is too large.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total < 2)
            {
                throw new InvalidDataException("Embedded uninstaller is empty.");
            }
        }

        await using var verification = new FileStream(
            destinationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 2,
            FileOptions.SequentialScan);
        if (verification.ReadByte() != 'M' || verification.ReadByte() != 'Z')
        {
            throw new InvalidDataException("Embedded uninstaller is not a Windows executable.");
        }
    }

    private static string NormalizeSha256(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length != 64 || normalized.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Installer package SHA-256 is invalid.");
        }

        return normalized.ToUpperInvariant();
    }

    private static void RestoreUninstaller(
        string uninstallerPath,
        string backupPath,
        bool hadOldUninstaller)
    {
        if (hadOldUninstaller && File.Exists(backupPath))
        {
            File.Copy(backupPath, uninstallerPath, overwrite: true);
        }
        else
        {
            File.Delete(uninstallerPath);
        }
    }

    private static void TryRollbackAction(Action action, List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is
                                           ArgumentException or
                                           IOException or
                                           InvalidDataException or
                                           InvalidOperationException or
                                           UnauthorizedAccessException or
                                           System.Security.SecurityException)
        {
            failures.Add(exception);
        }
    }

    private static void Report(
        IProgress<InstallationProgress>? progress,
        double percentage,
        string status) => progress?.Report(new InstallationProgress(percentage, status));
}
