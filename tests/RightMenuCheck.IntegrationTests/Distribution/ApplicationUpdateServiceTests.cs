using System.IO.Pipes;
using System.Security.Cryptography;
using RightMenuCheck.App.Services;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.IntegrationTests.Distribution;

public sealed class ApplicationUpdateServiceTests
{
    [Fact]
    public async Task ExpiredNewerManifestMapsToUnavailableState()
    {
        using var fixture = new DistributionDocumentClientTests.TemporaryDirectory();
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = signingKey.ExportPkcs8PrivateKeyPem();
        var publicKey = signingKey.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;
        var bytes = "package"u8.ToArray();
        var manifest = SignedUpdateManifest.Create(
            new UpdateManifestPayload(
                Sequence: 1,
                IssuedAtUtc: now.AddDays(-2),
                ExpiresAtUtc: now.AddDays(-1),
                "999.0.0",
                new UpdatePackage(
                    "update.zip",
                    bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes)),
                    "https://github.example/update.zip",
                    []),
                "Expired",
                "https://github.example/release"),
            privateKey);
        var service = new ApplicationUpdateService(
            CreateConfiguration(publicKey),
            new FakeDocumentClient(manifest, bytes),
            new FakeInstallContext(
                Environment.ProcessId,
                Path.Combine(fixture.Path, "RightMenuCheck.App.exe"),
                Path.Combine(fixture.Path, "RightMenuCheck.Updater.exe"),
                Path.Combine(fixture.Path, "updates"),
                Path.Combine(fixture.Path, "install")),
            new FakeUpdaterLauncher(),
            NullAppLogger.Instance);

        var check = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(ApplicationUpdateState.Unavailable, check.State);
        Assert.Equal(UpdateDecisionKind.StaleManifest, check.Decision?.Kind);
    }

    [Fact]
    public async Task PrepareCopiesHelperAndWritesSignedStructuredRequest()
    {
        using var fixture = new DistributionDocumentClientTests.TemporaryDirectory();
        var installDirectory = Path.Combine(fixture.Path, "install");
        Directory.CreateDirectory(installDirectory);
        var applicationPath = Path.Combine(installDirectory, "RightMenuCheck.App.exe");
        File.WriteAllText(applicationPath, "current");
        var updaterSource = Path.Combine(fixture.Path, "RightMenuCheck.Updater.exe");
        File.WriteAllText(updaterSource, "updater");
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = signingKey.ExportPkcs8PrivateKeyPem();
        var publicKey = signingKey.ExportSubjectPublicKeyInfoPem();
        var bytes = "package"u8.ToArray();
        var manifest = SignedUpdateManifest.Create(
            new UpdateManifestPayload(
                Sequence: 1,
                IssuedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddDays(30),
                "9.0.0",
                new UpdatePackage(
                    "update.zip",
                    bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes)),
                    "https://github.example/update.zip",
                    []),
                "Notes",
                "https://github.example/release"),
            privateKey);
        var documentClient = new FakeDocumentClient(manifest, bytes);
        var launcher = new FakeUpdaterLauncher();
        var installContext = new FakeInstallContext(
            ProcessId: 456,
            applicationPath,
            updaterSource,
            Path.Combine(fixture.Path, "updates"),
            TargetInstallDirectory: installDirectory);
        var service = new ApplicationUpdateService(
            CreateConfiguration(publicKey),
            documentClient,
            installContext,
            launcher,
            NullAppLogger.Instance);

        var check = await service.CheckAsync(CancellationToken.None);
        await service.PrepareAndLaunchAsync(manifest, progress: null, CancellationToken.None);

        Assert.Equal(UpdateDecisionKind.Required, check.Decision?.Kind);
        Assert.NotNull(launcher.RequestPath);
        var request = DistributionJson.Deserialize<UpdateInstallRequest>(
            File.ReadAllText(launcher.RequestPath));
        Assert.Equal(456, request.ParentProcessId);
        Assert.Equal(applicationPath, request.InstallDirectory + Path.DirectorySeparatorChar +
                                      UpdateInstallLocations.ApplicationFileName);
        Assert.True(request.Manifest.HasValidSignature(publicKey));
        Assert.Equal("updater", File.ReadAllText(launcher.UpdaterPath!));
    }

    private static EmbeddedDistributionConfiguration CreateConfiguration(string publicKey) => new(
        new AppDistributionSettings(
            AppDistributionSettings.CurrentSchemaVersion,
            "owner/repo",
            "main",
            "distribution/update.json",
            "distribution/messages.json",
            DistributionEndpoints.DefaultMirrorPrefixes,
            TelemetryBaseUrl: null),
        publicKey);

    private sealed class FakeDocumentClient(
        SignedUpdateManifest manifest,
        byte[] packageBytes) : IDistributionDocumentClient
    {
        public Task<T?> FetchVerifiedAsync<T>(
            IReadOnlyList<string> candidates,
            string cachePath,
            Func<T, bool> validator,
            Func<T, long> sequenceSelector,
            CancellationToken cancellationToken)
            where T : class => Task.FromResult((T?)(object)manifest);

        public Task<string> DownloadPackageAsync(
            UpdatePackage package,
            string destinationDirectory,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(destinationDirectory);
            var path = Path.Combine(destinationDirectory, package.AssetName);
            File.WriteAllBytes(path, packageBytes);
            progress?.Report(1);
            return Task.FromResult(path);
        }
    }

    private sealed class FakeUpdaterLauncher : IUpdaterLauncher
    {
        public string? UpdaterPath { get; private set; }

        public string? RequestPath { get; private set; }

        public Task? HandshakeTask { get; private set; }

        public void Launch(string updaterPath, string requestPath)
        {
            UpdaterPath = updaterPath;
            RequestPath = requestPath;
            var request = DistributionJson.Deserialize<UpdateInstallRequest>(
                File.ReadAllText(requestPath));
            HandshakeTask = Task.Run(async () =>
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    request.ReadyPipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(CancellationToken.None);
                await using var writer = new StreamWriter(pipe)
                {
                    AutoFlush = true,
                };
                await writer.WriteLineAsync(request.ReadyNonce);
            });
        }
    }

    private sealed record FakeInstallContext(
        int ProcessId,
        string ApplicationPath,
        string UpdaterSourcePath,
        string UpdateRoot,
        string TargetInstallDirectory) : IApplicationInstallContext;
}
