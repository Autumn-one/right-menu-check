using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RightMenuCheck.Installation;

namespace RightMenuCheck.Installation.Tests;

public sealed class InstallationServiceTests
{
    [Fact]
    public async Task InstallsValidatedPayloadAndReplacesExistingVersion()
    {
        using var fixture = new InstallerFixture();
        Directory.CreateDirectory(fixture.Paths.InstallDirectory);
        File.WriteAllText(
            Path.Combine(fixture.Paths.InstallDirectory, "old-version.txt"),
            "old");
        Directory.CreateDirectory(fixture.Paths.InstallerCacheDirectory);
        File.WriteAllText(fixture.Paths.UninstallerPath, "old-uninstaller");
        var progress = new RecordingProgress();

        var result = await fixture.Service.InstallAsync(
            fixture.Payload,
            fixture.Paths,
            new InstallationOptions(CreateDesktopShortcut: true),
            progress,
            CancellationToken.None);

        Assert.Equal("1.2.3", result.Version);
        Assert.True(File.Exists(result.ApplicationPath));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.InstallDirectory, "old-version.txt")));
        Assert.Equal(FixturePayload.UninstallerBytes, File.ReadAllBytes(fixture.Paths.UninstallerPath));
        Assert.True(fixture.Integration.EnsureStoppedCalled);
        Assert.True(fixture.Integration.ApplyCalled);
        Assert.False(fixture.Integration.RestoreCalled);
        Assert.Equal(100, progress.Values[^1].Percentage);
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task RestoresExistingVersionAndIntegrationWhenApplyFails()
    {
        using var fixture = new InstallerFixture { Integration = { ThrowDuringApply = true } };
        Directory.CreateDirectory(fixture.Paths.InstallDirectory);
        var oldMarker = Path.Combine(fixture.Paths.InstallDirectory, "old-version.txt");
        File.WriteAllText(oldMarker, "old");
        Directory.CreateDirectory(fixture.Paths.InstallerCacheDirectory);
        var oldUninstaller = Encoding.ASCII.GetBytes("MZ-old-uninstaller");
        File.WriteAllBytes(fixture.Paths.UninstallerPath, oldUninstaller);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.InstallAsync(
                fixture.Payload,
                fixture.Paths,
                new InstallationOptions(CreateDesktopShortcut: false),
                progress: null,
                CancellationToken.None));

        Assert.True(File.Exists(oldMarker));
        Assert.False(File.Exists(Path.Combine(fixture.Paths.InstallDirectory, "build-info.json")));
        Assert.Equal(oldUninstaller, File.ReadAllBytes(fixture.Paths.UninstallerPath));
        Assert.True(fixture.Integration.RestoreCalled);
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task PreservesPreviousVersionBackupWhenActivatedPayloadCannotBeRemoved()
    {
        using var fixture = new InstallerFixture
        {
            Integration = { HoldApplicationOpenDuringFailure = true },
        };
        Directory.CreateDirectory(fixture.Paths.InstallDirectory);
        File.WriteAllText(
            Path.Combine(fixture.Paths.InstallDirectory, "old-version.txt"),
            "old");

        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            fixture.Service.InstallAsync(
                fixture.Payload,
                fixture.Paths,
                new InstallationOptions(false),
                progress: null,
                CancellationToken.None));

        Assert.Contains("rollback did not complete", exception.Message, StringComparison.Ordinal);
        var backup = Assert.Single(Directory.GetDirectories(
            Path.Combine(fixture.Root, "Programs"),
            ".RightMenuCheck.backup-*"));
        Assert.True(File.Exists(Path.Combine(backup, "old-version.txt")));
        Assert.True(File.Exists(fixture.Paths.ApplicationPath));
        Assert.True(fixture.Integration.RestoreCalled);
    }

    [Fact]
    public async Task RejectsTraversalEntryBeforeActivation()
    {
        using var fixture = new InstallerFixture(
            package: FixturePayload.CreatePackage(archive =>
                FixturePayload.AddText(archive, "../escaped.txt", "escape")));
        var escaped = Path.Combine(fixture.Root, "escaped.txt");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.InstallAsync(
                fixture.Payload,
                fixture.Paths,
                new InstallationOptions(false),
                progress: null,
                CancellationToken.None));

        Assert.False(File.Exists(escaped));
        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
        Assert.False(fixture.Integration.ApplyCalled);
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task RejectsAlternateDataStreamEntryBeforeActivation()
    {
        using var fixture = new InstallerFixture(
            package: FixturePayload.CreatePackage(archive =>
                FixturePayload.AddText(archive, "settings:stream", "hidden")));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.InstallAsync(
                fixture.Payload,
                fixture.Paths,
                new InstallationOptions(false),
                progress: null,
                CancellationToken.None));

        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
        Assert.False(fixture.Integration.ApplyCalled);
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task RejectsPrivateConfigurationInPayload()
    {
        using var fixture = new InstallerFixture(
            package: FixturePayload.CreatePackage(archive =>
                FixturePayload.AddText(archive, "github-conf.json", "private")));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.InstallAsync(
                fixture.Payload,
                fixture.Paths,
                new InstallationOptions(false),
                progress: null,
                CancellationToken.None));

        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
        Assert.False(fixture.Integration.ApplyCalled);
        Assert.Empty(fixture.TransactionArtifacts());
    }

    [Fact]
    public async Task RejectsPackageWhoseHashDoesNotMatchSetupMetadata()
    {
        using var fixture = new InstallerFixture(expectedHash: new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.InstallAsync(
                fixture.Payload,
                fixture.Paths,
                new InstallationOptions(false),
                progress: null,
                CancellationToken.None));

        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
        Assert.False(fixture.Integration.ApplyCalled);
        Assert.Empty(fixture.TransactionArtifacts());
    }

    private sealed class InstallerFixture : IDisposable
    {
        public InstallerFixture(byte[]? package = null, string? expectedHash = null)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"RightMenuCheck-InstallerTests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Paths = new InstallationPaths(
                Path.Combine(Root, "Programs", "RightMenuCheck"),
                Path.Combine(Root, "Data", "Installer"),
                Path.Combine(Root, "Data", "Installer", "RightMenuCheck.Uninstaller.exe"),
                Path.Combine(Root, "StartMenu", "RightMenuCheck.lnk"),
                Path.Combine(Root, "Desktop", "RightMenuCheck.lnk"));
            var packageBytes = package ?? FixturePayload.CreatePackage();
            Payload = new FixturePayload(packageBytes, expectedHash);
            Integration = new FakeInstallIntegration();
            Service = new InstallationService(Integration);
        }

        public string Root { get; }

        public InstallationPaths Paths { get; }

        public FixturePayload Payload { get; }

        public FakeInstallIntegration Integration { get; }

        public InstallationService Service { get; }

        public string[] TransactionArtifacts()
        {
            var artifacts = new List<string>();
            var programs = Path.Combine(Root, "Programs");
            if (Directory.Exists(programs))
            {
                artifacts.AddRange(Directory.GetFileSystemEntries(
                    programs,
                    ".RightMenuCheck.*"));
            }

            var installer = Path.Combine(Root, "Data", "Installer");
            if (Directory.Exists(installer))
            {
                artifacts.AddRange(Directory.GetFileSystemEntries(installer, "*.new-*"));
            }

            return [.. artifacts];
        }

        public void Dispose()
        {
            Integration.Dispose();
            SafeFileTree.TryDeleteDirectory(Root);
        }
    }

    private sealed class FixturePayload(byte[] package, string? expectedHash)
        : IInstallationPayloadSource
    {
        public static readonly byte[] UninstallerBytes = [0x4D, 0x5A, 0x01, 0x02, 0x03];

        public string ExpectedVersion => "1.2.3";

        public string ExpectedPackageSha256 => expectedHash ??
            Convert.ToHexString(SHA256.HashData(package));

        public Stream OpenApplicationPackage() => new MemoryStream(package, writable: false);

        public Stream OpenUninstaller() => new MemoryStream(UninstallerBytes, writable: false);

        public static byte[] CreatePackage(Action<ZipArchive>? customize = null)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddBytes(archive, "RightMenuCheck.App.exe", [0x4D, 0x5A, 0x01]);
                AddText(
                    archive,
                    "build-info.json",
                    JsonSerializer.Serialize(new
                    {
                        product = "RightMenuCheck",
                        version = "1.2.3",
                        selfContained = true,
                    }));
                AddBytes(archive, "helpers/RightMenuCheck.Elevated.exe", [0x4D, 0x5A]);
                AddBytes(archive, "helpers/updater/RightMenuCheck.Updater.exe", [0x4D, 0x5A]);
                AddBytes(archive, "workers/x64/RightMenuCheck.Probe.Worker.exe", [0x4D, 0x5A]);
                AddBytes(archive, "workers/x86/RightMenuCheck.Probe.Worker.exe", [0x4D, 0x5A]);
                AddBytes(archive, "workers/arm64/RightMenuCheck.Probe.Worker.exe", [0x4D, 0x5A]);
                customize?.Invoke(archive);
            }

            return output.ToArray();
        }

        public static void AddText(ZipArchive archive, string name, string value) =>
            AddBytes(archive, name, Encoding.UTF8.GetBytes(value));

        private static void AddBytes(ZipArchive archive, string name, byte[] value)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            using var stream = entry.Open();
            stream.Write(value);
        }
    }

    private sealed class FakeInstallIntegration : IInstallIntegration, IDisposable
    {
        private FileStream? _heldApplication;

        public bool EnsureStoppedCalled { get; private set; }

        public bool ApplyCalled { get; private set; }

        public bool RestoreCalled { get; private set; }

        public bool ThrowDuringApply { get; set; }

        public bool HoldApplicationOpenDuringFailure { get; set; }

        public Task EnsureApplicationStoppedAsync(
            string applicationPath,
            CancellationToken cancellationToken)
        {
            _ = applicationPath;
            cancellationToken.ThrowIfCancellationRequested();
            EnsureStoppedCalled = true;
            return Task.CompletedTask;
        }

        public IInstallIntegrationSnapshot Capture(InstallationPaths paths)
        {
            _ = paths;
            return new FakeSnapshot();
        }

        public void Apply(
            InstallationPaths paths,
            string version,
            bool createDesktopShortcut,
            long estimatedSizeKilobytes)
        {
            _ = paths;
            _ = version;
            _ = createDesktopShortcut;
            _ = estimatedSizeKilobytes;
            ApplyCalled = true;
            if (HoldApplicationOpenDuringFailure)
            {
                _heldApplication = new FileStream(
                    paths.ApplicationPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                throw new InvalidOperationException("Synthetic locked-file integration failure.");
            }

            if (ThrowDuringApply)
            {
                throw new InvalidOperationException("Synthetic integration failure.");
            }
        }

        public void Restore(InstallationPaths paths, IInstallIntegrationSnapshot snapshot)
        {
            _ = paths;
            _ = Assert.IsType<FakeSnapshot>(snapshot);
            RestoreCalled = true;
        }

        public void Dispose() => _heldApplication?.Dispose();

        private sealed class FakeSnapshot : IInstallIntegrationSnapshot;
    }

    private sealed class RecordingProgress : IProgress<InstallationProgress>
    {
        public List<InstallationProgress> Values { get; } = [];

        public void Report(InstallationProgress value) => Values.Add(value);
    }
}
