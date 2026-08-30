using System.Security.Cryptography;
using RightMenuCheck.Distribution;
using RightMenuCheck.Updater;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.Updater.Tests;

public sealed class UpdateInstallerTests
{
    [Fact]
    public async Task InstallsNewDirectoryAndRemovesBackupAfterHealthyStart()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);

        var result = await fixture.InstallAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.RolledBack);
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "payload.txt")));
        Assert.False(File.Exists(fixture.PackagePath));
        Assert.Single(fixture.ProcessController.StartedExecutables);
    }

    [Fact]
    public async Task RestoresPreviousDirectoryWhenHealthyStartIsNotObserved()
    {
        using var fixture = new InstallerFixture(healthSucceeds: false);

        var result = await fixture.InstallAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "payload.txt")));
        Assert.Equal(2, fixture.ProcessController.StartedExecutables.Count);
        Assert.Single(fixture.ProcessController.StoppedProcesses);
    }

    [Fact]
    public async Task RestoresAndRestartsPreviousVersionWhenNewProcessCannotStart()
    {
        using var fixture = new InstallerFixture(
            healthSucceeds: true,
            failFirstStart: true);

        var result = await fixture.InstallAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "payload.txt")));
        Assert.Equal(2, fixture.ProcessController.StartAttempts);
        Assert.Single(fixture.ProcessController.StartedExecutables);
    }

    [Fact]
    public async Task RejectsPackageChangedAfterManifestBeforeStoppingApplication()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);

        var result = await fixture.InstallAsync(tamperAfterManifest: true);

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "payload.txt")));
        Assert.Equal(0, fixture.ProcessController.WaitCalls);
    }

    private sealed class InstallerFixture : IDisposable
    {
        private readonly SafeZipExtractorTests.TemporaryDirectory _temporary = new();
        private readonly FakeHealthMonitor _healthMonitor;
        private readonly string _privateKey;
        private readonly string _publicKey;

        public InstallerFixture(bool healthSucceeds, bool failFirstStart = false)
        {
            InstallDirectory = Path.Combine(_temporary.Path, "RightMenuCheck");
            Directory.CreateDirectory(InstallDirectory);
            File.WriteAllText(Path.Combine(InstallDirectory, "RightMenuCheck.App.exe"), "old exe");
            File.WriteAllText(Path.Combine(InstallDirectory, "payload.txt"), "old");
            PackagePath = Path.Combine(_temporary.Path, "update.zip");
            SafeZipExtractorTests.CreateArchive(
                PackagePath,
                ("RightMenuCheck.App.exe", "new exe"),
                ("payload.txt", "new"),
                ("build-info.json", "{\"version\":\"1.1.0\"}"));
            using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _privateKey = signingKey.ExportPkcs8PrivateKeyPem();
            _publicKey = signingKey.ExportSubjectPublicKeyInfoPem();
            ProcessController = new FakeProcessController
            {
                FailFirstStart = failFirstStart,
            };
            _healthMonitor = new FakeHealthMonitor(_temporary.Path, healthSucceeds);
        }

        public string InstallDirectory { get; }

        public string PackagePath { get; }

        public FakeProcessController ProcessController { get; }

        public Task<UpdateInstallResult> InstallAsync(bool tamperAfterManifest = false)
        {
            var packageInfo = new FileInfo(PackagePath);
            var packageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PackagePath)));
            var manifest = SignedUpdateManifest.Create(
                new UpdateManifestPayload(
                    "1.1.0",
                    DateTimeOffset.UtcNow,
                    new UpdatePackage(
                        packageInfo.Name,
                        packageInfo.Length,
                        packageHash,
                        "https://github.com/owner/repo/releases/download/v1.1.0/update.zip",
                        []),
                    "Fixture",
                    "https://github.com/owner/repo/releases/tag/v1.1.0"),
                _privateKey);
            if (tamperAfterManifest)
            {
                File.AppendAllText(PackagePath, "tampered");
            }

            var request = new UpdateInstallRequest(
                UpdateInstallRequest.CurrentSchemaVersion,
                ParentProcessId: 123,
                PackagePath,
                InstallDirectory,
                "RightMenuCheck.App.exe",
                manifest,
                Guid.NewGuid().ToString("N"));
            var installer = new UpdateInstaller(
                new SafeZipExtractor(),
                ProcessController,
                _healthMonitor,
                _publicKey,
                NullAppLogger.Instance);
            return installer.InstallAsync(request, CancellationToken.None);
        }

        public void Dispose() => _temporary.Dispose();
    }

    internal sealed class FakeProcessController : IUpdateProcessController
    {
        private int _nextProcessId = 1000;

        public bool FailFirstStart { get; init; }

        public int StartAttempts { get; private set; }

        public int WaitCalls { get; private set; }

        public List<string> StartedExecutables { get; } = [];

        public List<UpdateProcessHandle> StoppedProcesses { get; } = [];

        public Task WaitForExitAsync(
            int processId,
            string expectedExecutablePath,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WaitCalls++;
            return Task.CompletedTask;
        }

        public UpdateProcessHandle Start(
            string executablePath,
            string workingDirectory,
            IReadOnlyList<string> arguments)
        {
            StartAttempts++;
            if (FailFirstStart && StartAttempts == 1)
            {
                throw new InvalidOperationException("Synthetic process start failure.");
            }

            StartedExecutables.Add(executablePath);
            return new UpdateProcessHandle(_nextProcessId++, executablePath);
        }

        public bool HasExited(UpdateProcessHandle process) => false;

        public Task StopAsync(
            UpdateProcessHandle process,
            CancellationToken cancellationToken)
        {
            StoppedProcesses.Add(process);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHealthMonitor(string root, bool succeeds) : IUpdateHealthMonitor
    {
        public string GetMarkerPath(string healthToken) =>
            Path.Combine(root, $"health-{healthToken}.ok");

        public Task<bool> WaitForHealthyAsync(
            string healthToken,
            UpdateProcessHandle process,
            IUpdateProcessController processController,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(succeeds);
    }
}
