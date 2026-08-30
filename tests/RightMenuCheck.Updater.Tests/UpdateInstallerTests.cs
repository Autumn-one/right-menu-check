using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
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

    [Fact]
    public async Task InvalidSignedArchiveDoesNotSignalReadyOrStopOldVersion()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        File.WriteAllText(fixture.PackagePath, "signed but not a zip archive");

        var result = await fixture.InstallAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("old", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "payload.txt")));
        Assert.Equal(0, fixture.ProcessController.WaitCalls);
        Assert.Empty(fixture.ProcessController.StartedExecutables);
    }

    [Fact]
    public async Task ExpiredNewerManifestIsRejectedBeforeReadyOrJournalCreation()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        var ready = new CountingReadySignal();
        var request = fixture.CreateRequest(expired: true);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.InstallAsync(request: request, readySignal: ready));

        Assert.Equal(0, ready.SignalCount);
        Assert.Equal(0, fixture.ProcessController.WaitCalls);
        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.InstallDirectory,
            "payload.txt")));
        Assert.Empty(Directory.GetFiles(
            fixture.TemporaryRoot,
            ".rightmenucheck-update-*.json",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task MigratesProtectedSourceToPerUserTargetWithoutChangingSource()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true, migrate: true);

        var result = await fixture.InstallAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("new", File.ReadAllText(Path.Combine(fixture.InstallDirectory, "payload.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.SourceInstallDirectory,
            "payload.txt")));
    }

    [Fact]
    public async Task FailedMigrationRemovesTargetAndRestartsProtectedSource()
    {
        using var fixture = new InstallerFixture(healthSucceeds: false, migrate: true);

        var result = await fixture.InstallAsync();

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.False(Directory.Exists(fixture.InstallDirectory));
        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.SourceInstallDirectory,
            "payload.txt")));
        Assert.Equal(2, fixture.ProcessController.StartedExecutables.Count);
        Assert.Equal(
            Path.Combine(fixture.SourceInstallDirectory, "RightMenuCheck.App.exe"),
            fixture.ProcessController.StartedExecutables[1]);
    }

    [Theory]
    [InlineData(UpdateTransactionPhase.StagingStarted)]
    [InlineData(UpdateTransactionPhase.StagingPrepared)]
    public async Task RecoversAbandonedStagingAndCompletesTheNextAttempt(
        UpdateTransactionPhase crashPhase)
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        var request = fixture.CreateRequest();

        await Assert.ThrowsAsync<SimulatedUpdateCrashException>(() =>
            fixture.InstallAsync(
                request: request,
                observer: new CrashAfterPhaseObserver(crashPhase)));

        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.InstallDirectory,
            "payload.txt")));
        var journalPath = fixture.JournalPath;
        Assert.True(File.Exists(journalPath));

        var result = await fixture.InstallAsync(request: request);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("new", File.ReadAllText(Path.Combine(
            fixture.InstallDirectory,
            "payload.txt")));
        Assert.False(File.Exists(journalPath));
    }

    [Theory]
    [InlineData(UpdateTransactionPhase.BackupMoveStarted)]
    [InlineData(UpdateTransactionPhase.BackupMoved)]
    [InlineData(UpdateTransactionPhase.ActivationMoveStarted)]
    [InlineData(UpdateTransactionPhase.NewActive)]
    public async Task RestoresPreviousVersionAfterCrashAroundDirectoryMoves(
        UpdateTransactionPhase crashPhase)
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        var request = fixture.CreateRequest();

        await Assert.ThrowsAsync<SimulatedUpdateCrashException>(() =>
            fixture.InstallAsync(
                request: request,
                observer: new CrashAfterPhaseObserver(crashPhase)));
        var journalPath = fixture.JournalPath;

        var result = await fixture.InstallAsync(request: request);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal("InterruptedUpdateRecovered", result.ErrorType);
        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.InstallDirectory,
            "payload.txt")));
        Assert.False(File.Exists(journalPath));
    }

    [Theory]
    [InlineData(UpdateTransactionPhase.RollbackRestoreStarted)]
    [InlineData(UpdateTransactionPhase.RollbackCleanupStarted)]
    public async Task CompletesRollbackInterruptedAtPersistedRecoveryPhase(
        UpdateTransactionPhase crashPhase)
    {
        using var fixture = new InstallerFixture(healthSucceeds: false);
        var request = fixture.CreateRequest();

        await Assert.ThrowsAsync<SimulatedUpdateCrashException>(() =>
            fixture.InstallAsync(
                request: request,
                observer: new CrashAfterPhaseObserver(crashPhase)));
        var journalPath = fixture.JournalPath;

        var result = await fixture.InstallAsync(request: request);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.InstallDirectory,
            "payload.txt")));
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public async Task KeepsNewVersionWhenCrashOccursAfterHealthConfirmation()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        var request = fixture.CreateRequest();

        await Assert.ThrowsAsync<SimulatedUpdateCrashException>(() =>
            fixture.InstallAsync(
                request: request,
                observer: new CrashAfterPhaseObserver(
                    UpdateTransactionPhase.HealthConfirmed)));
        var journalPath = fixture.JournalPath;
        File.Delete(fixture.PackagePath);

        var result = await fixture.InstallAsync(request: request);

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("new", File.ReadAllText(Path.Combine(
            fixture.InstallDirectory,
            "payload.txt")));
        Assert.False(File.Exists(journalPath));
    }

    [Fact]
    public async Task InterruptedMigrationRemovesNewTargetAndRestartsProtectedSource()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true, migrate: true);
        var request = fixture.CreateRequest();
        await Assert.ThrowsAsync<SimulatedUpdateCrashException>(() =>
            fixture.InstallAsync(
                request: request,
                observer: new CrashAfterPhaseObserver(
                    UpdateTransactionPhase.NewActive)));

        var result = await fixture.InstallAsync(request: request);

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.False(Directory.Exists(fixture.InstallDirectory));
        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.SourceInstallDirectory,
            "payload.txt")));
        Assert.Equal(
            Path.Combine(fixture.SourceInstallDirectory, "RightMenuCheck.App.exe"),
            fixture.ProcessController.StartedExecutables[^1]);
    }

    [Fact]
    public async Task RecoveryBeforeReadyDoesNotStartDuplicateWhenParentIsVerified()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        var request = fixture.CreateRequest();
        await Assert.ThrowsAsync<SimulatedUpdateCrashException>(() =>
            fixture.InstallAsync(
                request: request,
                observer: new CrashAfterPhaseObserver(
                    UpdateTransactionPhase.NewActive)));
        fixture.ProcessController.ParentIsRunning = true;
        var ready = new CountingReadySignal();
        var startsBeforeRecovery = fixture.ProcessController.StartAttempts;

        var result = await fixture.InstallAsync(request: request, readySignal: ready);

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Equal(0, ready.SignalCount);
        Assert.Equal(startsBeforeRecovery, fixture.ProcessController.StartAttempts);
        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.InstallDirectory,
            "payload.txt")));
    }

    [Fact]
    public async Task RejectsConcurrentUpdaterForTheSameValidatedTargetBeforeReady()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        var blockingReady = new BlockingReadySignal();
        var competingReady = new CountingReadySignal();
        var first = fixture.InstallAsync(
            request: fixture.CreateRequest(),
            readySignal: blockingReady);
        await blockingReady.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var competing = await fixture.InstallAsync(
                request: fixture.CreateRequest(),
                readySignal: competingReady);

            Assert.False(competing.Succeeded);
            Assert.Equal(nameof(UpdateTargetBusyException), competing.ErrorType);
            Assert.Equal(0, competingReady.SignalCount);
            Assert.Equal(0, fixture.ProcessController.WaitCalls);
        }
        finally
        {
            blockingReady.Release();
        }

        var firstResult = await first;
        Assert.True(firstResult.Succeeded, firstResult.Message);
    }

    [Fact]
    public async Task HoldsTargetLockUntilHandledFailureRecoveryCompletes()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        using var observer = new BlockingRecoveryObserver();
        var competingReady = new CountingReadySignal();
        var first = Task.Run(() => fixture.InstallAsync(observer: observer));
        Assert.True(observer.WaitForRecovery(TimeSpan.FromSeconds(5)));

        try
        {
            var competing = await fixture.InstallAsync(
                request: fixture.CreateRequest(),
                readySignal: competingReady);

            Assert.False(competing.Succeeded);
            Assert.Equal(nameof(UpdateTargetBusyException), competing.ErrorType);
            Assert.Equal(0, competingReady.SignalCount);
        }
        finally
        {
            observer.Release();
        }

        var firstResult = await first;
        Assert.False(firstResult.Succeeded);
        Assert.Equal(nameof(InvalidOperationException), firstResult.ErrorType);
        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.InstallDirectory,
            "payload.txt")));
    }

    [Fact]
    public async Task RejectsTamperedJournalPathsWithoutDeletingTheReferencedDirectory()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        var request = fixture.CreateRequest();
        await Assert.ThrowsAsync<SimulatedUpdateCrashException>(() =>
            fixture.InstallAsync(
                request: request,
                observer: new CrashAfterPhaseObserver(
                    UpdateTransactionPhase.StagingPrepared)));
        var protectedDirectory = Path.Combine(fixture.TemporaryRoot, "must-not-delete");
        Directory.CreateDirectory(protectedDirectory);
        File.WriteAllText(Path.Combine(protectedDirectory, "evidence.txt"), "keep");
        var journal = JsonNode.Parse(File.ReadAllText(fixture.JournalPath))!.AsObject();
        journal["stagingDirectory"] = protectedDirectory;
        File.WriteAllText(fixture.JournalPath, journal.ToJsonString());
        var ready = new CountingReadySignal();

        var result = await fixture.InstallAsync(request: request, readySignal: ready);

        Assert.False(result.Succeeded);
        Assert.Equal("RecoveryFailed", result.ErrorType);
        Assert.Equal(0, ready.SignalCount);
        Assert.True(File.Exists(Path.Combine(protectedDirectory, "evidence.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.InstallDirectory,
            "payload.txt")));
    }

    [Fact]
    public async Task RejectsNullJournalIdentityAsControlledFailure()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        var request = fixture.CreateRequest();
        await Assert.ThrowsAsync<SimulatedUpdateCrashException>(() =>
            fixture.InstallAsync(
                request: request,
                observer: new CrashAfterPhaseObserver(
                    UpdateTransactionPhase.StagingStarted)));
        var journal = JsonNode.Parse(File.ReadAllText(fixture.JournalPath))!.AsObject();
        journal["targetKey"] = null;
        File.WriteAllText(fixture.JournalPath, journal.ToJsonString());

        var result = await fixture.InstallAsync(request: request);

        Assert.False(result.Succeeded);
        Assert.Equal("RecoveryFailed", result.ErrorType);
        Assert.Equal("old", File.ReadAllText(Path.Combine(
            fixture.InstallDirectory,
            "payload.txt")));
    }

    [Fact]
    public async Task TransactionLogsContainPhaseAndTargetKeyWithoutFilesystemPaths()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        var logDirectory = Path.Combine(fixture.TemporaryRoot, "logs");
        using var logger = new StructuredFileLogger("updater-test", logDirectory);

        var result = await fixture.InstallAsync(logger: logger);
        await logger.FlushAsync(CancellationToken.None);
        logger.Dispose();

        Assert.True(result.Succeeded, result.Message);
        var logText = string.Join(
            Environment.NewLine,
            Directory.GetFiles(logDirectory, "*.jsonl").Select(File.ReadAllText));
        Assert.Contains("\"eventName\":\"update.transaction_phase\"", logText);
        Assert.Contains("\"phase\":", logText);
        Assert.Contains("\"targetKey\":", logText);
        Assert.DoesNotContain(fixture.TemporaryRoot, logText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EveryPersistedJournalSnapshotIsParseableAndAtomicallyReplaced()
    {
        using var fixture = new InstallerFixture(healthSucceeds: true);
        var observer = new JournalSnapshotObserver(fixture.TemporaryRoot);

        var result = await fixture.InstallAsync(observer: observer);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(observer.SnapshotCount >= 10);
        Assert.Empty(Directory.GetFiles(
            fixture.TemporaryRoot,
            ".rightmenucheck-update-*.json.tmp",
            SearchOption.TopDirectoryOnly));
    }

    private sealed class InstallerFixture : IDisposable
    {
        private readonly SafeZipExtractorTests.TemporaryDirectory _temporary = new();
        private readonly FakeHealthMonitor _healthMonitor;
        private readonly string _privateKey;
        private readonly string _publicKey;

        public InstallerFixture(
            bool healthSucceeds,
            bool failFirstStart = false,
            bool migrate = false)
        {
            SourceInstallDirectory = Path.Combine(_temporary.Path, "RightMenuCheck-Source");
            InstallDirectory = migrate
                ? Path.Combine(_temporary.Path, "RightMenuCheck-Target")
                : SourceInstallDirectory;
            Directory.CreateDirectory(SourceInstallDirectory);
            File.WriteAllText(
                Path.Combine(SourceInstallDirectory, "RightMenuCheck.App.exe"),
                "old exe");
            File.WriteAllText(Path.Combine(SourceInstallDirectory, "payload.txt"), "old");
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
            _healthMonitor = new FakeHealthMonitor(healthSucceeds);
        }

        public string InstallDirectory { get; }

        public string JournalPath => Directory.GetFiles(
            _temporary.Path,
            ".rightmenucheck-update-*.json",
            SearchOption.TopDirectoryOnly).Single();

        public string SourceInstallDirectory { get; }

        public string PackagePath { get; }

        public string TemporaryRoot => _temporary.Path;

        public FakeProcessController ProcessController { get; }

        public UpdateInstallRequest CreateRequest(
            bool tamperAfterManifest = false,
            bool expired = false)
        {
            var packageInfo = new FileInfo(PackagePath);
            var packageHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PackagePath)));
            var issuedAt = expired
                ? DateTimeOffset.UtcNow.AddDays(-2)
                : DateTimeOffset.UtcNow;
            var expiresAt = expired
                ? DateTimeOffset.UtcNow.AddDays(-1)
                : DateTimeOffset.UtcNow.AddDays(30);
            var manifest = SignedUpdateManifest.Create(
                new UpdateManifestPayload(
                    Sequence: 1,
                    IssuedAtUtc: issuedAt,
                    ExpiresAtUtc: expiresAt,
                    "1.1.0",
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

            return new UpdateInstallRequest(
                UpdateInstallRequest.CurrentSchemaVersion,
                ParentProcessId: 123,
                PackagePath,
                Path.Combine(SourceInstallDirectory, "RightMenuCheck.App.exe"),
                InstallDirectory,
                manifest,
                Guid.NewGuid().ToString("N"),
                "fixture.ready.pipe",
                "fixture-ready-nonce");
        }

        public Task<UpdateInstallResult> InstallAsync(
            bool tamperAfterManifest = false,
            UpdateInstallRequest? request = null,
            IUpdateTransactionObserver? observer = null,
            IUpdateReadySignal? readySignal = null,
            IAppLogger? logger = null)
        {
            request ??= CreateRequest(tamperAfterManifest);
            var installer = new UpdateInstaller(
                new SafeZipExtractor(),
                ProcessController,
                _healthMonitor,
                new FixtureTargetPolicy(_temporary.Path),
                readySignal ?? new FakeReadySignal(),
                _publicKey,
                logger ?? NullAppLogger.Instance,
                observer);
            return installer.InstallAsync(request, CancellationToken.None);
        }

        public void Dispose() => _temporary.Dispose();
    }

    internal sealed class FakeProcessController : IUpdateProcessController
    {
        private int _nextProcessId = 1000;

        public bool FailFirstStart { get; init; }

        public bool ParentIsRunning { get; set; } = true;

        public int StartAttempts { get; private set; }

        public int WaitCalls { get; private set; }

        public List<string> StartedExecutables { get; } = [];

        public List<UpdateProcessHandle> StoppedProcesses { get; } = [];

        public IVerifiedUpdateParent OpenVerifiedParent(
            int processId,
            string expectedExecutablePath)
        {
            if (!ParentIsRunning)
            {
                throw new InvalidOperationException("Synthetic parent process has exited.");
            }

            return new FakeVerifiedParent(this);
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

        private sealed class FakeVerifiedParent(FakeProcessController owner)
            : IVerifiedUpdateParent
        {
            public Task WaitForExitAsync(
                TimeSpan timeout,
                CancellationToken cancellationToken)
            {
                owner.WaitCalls++;
                owner.ParentIsRunning = false;
                return Task.CompletedTask;
            }

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeHealthMonitor(bool succeeds) : IUpdateHealthMonitor
    {
        public UpdateHealthEndpoint Create(string healthToken) => new(
            $"fixture.health.{Guid.NewGuid():N}",
            healthToken,
            new NamedPipeServerStream(
                $"fixture.health.server.{Guid.NewGuid():N}",
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous));

        public Task<bool> WaitForHealthyAsync(
            UpdateHealthEndpoint endpoint,
            UpdateProcessHandle process,
            string expectedVersion,
            IUpdateProcessController processController,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(succeeds);
    }

    private sealed class FakeReadySignal : IUpdateReadySignal
    {
        public Task SignalAsync(
            string pipeName,
            string nonce,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CountingReadySignal : IUpdateReadySignal
    {
        public int SignalCount { get; private set; }

        public Task SignalAsync(
            string pipeName,
            string nonce,
            CancellationToken cancellationToken)
        {
            SignalCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingReadySignal : IUpdateReadySignal
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async Task SignalAsync(
            string pipeName,
            string nonce,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class CrashAfterPhaseObserver(UpdateTransactionPhase crashPhase)
        : IUpdateTransactionObserver
    {
        public void OnPhasePersisted(UpdateTransactionPhase phase, string targetKey)
        {
            if (phase == crashPhase)
            {
                throw new SimulatedUpdateCrashException(phase);
            }
        }
    }

    private sealed class SimulatedUpdateCrashException(UpdateTransactionPhase phase)
        : Exception($"Simulated updater crash after {phase}.");

    private sealed class BlockingRecoveryObserver : IUpdateTransactionObserver, IDisposable
    {
        private readonly ManualResetEventSlim _recoveryEntered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _failureInjected;

        public void OnPhasePersisted(UpdateTransactionPhase phase, string targetKey)
        {
            if (phase == UpdateTransactionPhase.StagingPrepared &&
                Interlocked.Exchange(ref _failureInjected, 1) == 0)
            {
                throw new InvalidOperationException("Synthetic handled update failure.");
            }

            if (phase == UpdateTransactionPhase.StagingCleanupStarted)
            {
                _recoveryEntered.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Synthetic recovery release timed out.");
                }
            }
        }

        public bool WaitForRecovery(TimeSpan timeout) => _recoveryEntered.Wait(timeout);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _recoveryEntered.Dispose();
            _release.Dispose();
        }
    }

    private sealed class JournalSnapshotObserver(string targetParent)
        : IUpdateTransactionObserver
    {
        public int SnapshotCount { get; private set; }

        public void OnPhasePersisted(UpdateTransactionPhase phase, string targetKey)
        {
            var journalPath = Assert.Single(Directory.GetFiles(
                targetParent,
                ".rightmenucheck-update-*.json",
                SearchOption.TopDirectoryOnly));
            var snapshot = JsonNode.Parse(File.ReadAllText(journalPath));
            Assert.NotNull(snapshot);
            Assert.Empty(Directory.GetFiles(
                targetParent,
                ".rightmenucheck-update-*.json.tmp",
                SearchOption.TopDirectoryOnly));
            SnapshotCount++;
        }
    }

    private sealed class FixtureTargetPolicy(string fixtureRoot) : IUpdateTargetPolicy
    {
        public string ResolveTarget(string parentApplicationPath, string requestedInstallDirectory)
        {
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedInstallDirectory));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixtureRoot)) +
                       Path.DirectorySeparatorChar;
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Fixture target escaped the temporary root.");
            }

            return target;
        }
    }
}
