using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using RightMenuCheck.Distribution;
using RightMenuCheck.ReleaseManager.Configuration;
using RightMenuCheck.ReleaseManager.GitHub;
using RightMenuCheck.ReleaseManager.Publishing;
using RightMenuCheck.ReleaseManager.Services;
using RightMenuCheck.ReleaseManager.Tests.TestSupport;

namespace RightMenuCheck.ReleaseManager.Tests.Publishing;

public sealed class ReleasePublishingServiceTests
{
    private static readonly DateTimeOffset TestNow = DateTimeOffset.Parse(
        "2026-08-31T01:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public async Task BuildsPackagesSignsManifestAndPublishesControlFileLast()
    {
        using var directory = new TemporaryDirectory();
        var keys = CreateKeys();
        var github = new FakeGitHubRepositoryClient
        {
            RepositoryFile = CreateRemoteManifest(
                keys.PrivateKey,
                version: "0.0.9",
                sequence: 17,
                sha: "previous-sha"),
        };
        var runner = new FakePublishScriptRunner("0.1.0");
        var service = CreateService(directory.Path, github, runner, keys.PrivateKey);
        var progress = new List<ReleasePublishingStage>();

        var result = await service.PublishAsync(
            new ReleasePublishingRequest("0.1.0", "Release notes", IsPrerelease: false),
            new Progress<ReleasePublishingProgress>(value => progress.Add(value.Stage)),
            CancellationToken.None);

        Assert.Equal("v0.1.0", github.LastCreateRequest?.TagName);
        Assert.Equal("main", github.LastCreateRequest?.TargetCommitish);
        Assert.Equal(
            [
                "get-file:distribution/update.json:main",
                "create-release",
                "upload:501",
                "put-file:distribution/update.json:main",
            ],
            github.Calls);
        Assert.Equal("previous-sha", github.LastPutRequest?.ExistingSha);
        Assert.Equal("application/zip", result.UploadedAsset.ContentType);
        Assert.True(File.Exists(result.Artifact.ArchivePath));
        Assert.Equal(64, result.Artifact.Sha256.Length);
        Assert.True(result.Manifest.HasValidSignature(keys.PublicKey));
        Assert.Equal(18, result.Manifest.Payload.Sequence);
        Assert.Equal(TestNow, result.Manifest.Payload.IssuedAtUtc);
        Assert.Equal(TestNow.AddDays(365), result.Manifest.Payload.ExpiresAtUtc);
        Assert.Equal(result.Artifact.Sha256, result.Manifest.Payload.Package.Sha256);
        Assert.Equal(
            "https://github.com/owner/repo/releases/download/v0.1.0/RightMenuCheck-0.1.0-win-x64.zip",
            result.Manifest.Payload.Package.PrimaryUrl);
        Assert.Equal(
            "https://mirror.example/https://github.com/owner/repo/releases/download/v0.1.0/RightMenuCheck-0.1.0-win-x64.zip",
            Assert.Single(result.Manifest.Payload.Package.MirrorUrls));

        using var archive = ZipFile.OpenRead(result.Artifact.ArchivePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "RightMenuCheck.App.exe");
        Assert.DoesNotContain(archive.Entries, entry =>
            entry.FullName.Contains(".secrets", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.Contains("github-conf", StringComparison.OrdinalIgnoreCase));

        var savedManifest = DistributionJson.Deserialize<SignedUpdateManifest>(
            System.Text.Encoding.UTF8.GetString(github.LastPutRequest!.Content));
        Assert.True(savedManifest.HasValidSignature(keys.PublicKey));
        Assert.Contains(ReleasePublishingStage.Validating, progress);
        Assert.Contains(ReleasePublishingStage.Completed, progress);
    }

    [Fact]
    public async Task FirstManifestStartsAtSequenceOne()
    {
        using var directory = new TemporaryDirectory();
        var keys = CreateKeys();
        var github = new FakeGitHubRepositoryClient();
        var service = CreateService(
            directory.Path,
            github,
            new FakePublishScriptRunner("0.1.0"),
            keys.PrivateKey);

        var result = await service.PublishAsync(
            new ReleasePublishingRequest("0.1.0", string.Empty, IsPrerelease: false),
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, result.Manifest.Payload.Sequence);
        Assert.Null(github.LastPutRequest?.ExistingSha);
    }

    [Fact]
    public async Task ExpiredRemoteManifestStillAllowsPublishingAHigherVersion()
    {
        using var directory = new TemporaryDirectory();
        var keys = CreateKeys();
        var github = new FakeGitHubRepositoryClient
        {
            RepositoryFile = CreateRemoteManifest(
                keys.PrivateKey,
                version: "0.0.9",
                sequence: 23,
                sha: "expired-sha",
                expiresAtUtc: TestNow.AddMinutes(-1)),
        };
        var service = CreateService(
            directory.Path,
            github,
            new FakePublishScriptRunner("0.1.0"),
            keys.PrivateKey);

        var result = await service.PublishAsync(
            new ReleasePublishingRequest("0.1.0", string.Empty, IsPrerelease: false),
            progress: null,
            CancellationToken.None);

        Assert.Equal(24, result.Manifest.Payload.Sequence);
        Assert.Equal("expired-sha", github.LastPutRequest?.ExistingSha);
    }

    [Fact]
    public async Task RemoteVersionRollbackStopsBeforeRemoteMutation()
    {
        using var directory = new TemporaryDirectory();
        var keys = CreateKeys();
        var github = new FakeGitHubRepositoryClient
        {
            RepositoryFile = CreateRemoteManifest(
                keys.PrivateKey,
                version: "0.2.0",
                sequence: 8,
                sha: "current-sha"),
        };
        var service = CreateService(
            directory.Path,
            github,
            new FakePublishScriptRunner("0.1.0"),
            keys.PrivateKey);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishAsync(
                new ReleasePublishingRequest("0.1.0", string.Empty, IsPrerelease: false),
                progress: null,
                CancellationToken.None));

        Assert.Contains("必须更高", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["get-file:distribution/update.json:main"], github.Calls);
        Assert.Null(github.LastCreateRequest);
        Assert.Empty(github.PutRequests);
    }

    [Fact]
    public async Task InvalidRemoteManifestSignatureStopsBeforeRemoteMutation()
    {
        using var directory = new TemporaryDirectory();
        var keys = CreateKeys();
        var otherKeys = CreateKeys();
        var github = new FakeGitHubRepositoryClient
        {
            RepositoryFile = CreateRemoteManifest(
                otherKeys.PrivateKey,
                version: "0.0.9",
                sequence: 8,
                sha: "untrusted-sha"),
        };
        var service = CreateService(
            directory.Path,
            github,
            new FakePublishScriptRunner("0.1.0"),
            keys.PrivateKey);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PublishAsync(
                new ReleasePublishingRequest("0.1.0", string.Empty, IsPrerelease: false),
                progress: null,
                CancellationToken.None));

        Assert.Contains("签名或内容无效", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["get-file:distribution/update.json:main"], github.Calls);
        Assert.Null(github.LastCreateRequest);
        Assert.Empty(github.PutRequests);
    }

    [Fact]
    public async Task VersionMismatchStopsBeforeAnyRemoteMutation()
    {
        using var directory = new TemporaryDirectory();
        var keys = CreateKeys();
        var github = new FakeGitHubRepositoryClient();
        var runner = new FakePublishScriptRunner("9.9.9");
        var service = CreateService(directory.Path, github, runner, keys.PrivateKey);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishAsync(
                new ReleasePublishingRequest("0.1.0", string.Empty, IsPrerelease: false),
                progress: null,
                CancellationToken.None));

        Assert.Contains("产物版本为 9.9.9", exception.Message, StringComparison.Ordinal);
        Assert.Empty(github.Calls);
        Assert.Equal(1, runner.InvocationCount);
    }

    [Fact]
    public async Task InvalidSigningKeyStopsBeforeBuildOrRemoteMutation()
    {
        using var directory = new TemporaryDirectory();
        var github = new FakeGitHubRepositoryClient();
        var runner = new FakePublishScriptRunner("0.1.0");
        var service = CreateService(directory.Path, github, runner, "not-a-private-key");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PublishAsync(
                new ReleasePublishingRequest("0.1.0", string.Empty, IsPrerelease: false),
                progress: null,
                CancellationToken.None));

        Assert.Equal("分发签名私钥无效。", exception.Message);
        Assert.DoesNotContain("not-a-private-key", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
        Assert.Empty(github.Calls);
    }

    [Fact]
    public async Task CancellationAfterReleaseCreationReportsExactRemotePartialRelease()
    {
        using var directory = new TemporaryDirectory();
        var keys = CreateKeys();
        var github = new FakeGitHubRepositoryClient
        {
            UploadException = new OperationCanceledException("upload canceled"),
        };
        var service = CreateService(
            directory.Path,
            github,
            new FakePublishScriptRunner("0.1.0"),
            keys.PrivateKey);

        var exception = await Assert.ThrowsAsync<RemoteReleaseIncompleteException>(() =>
            service.PublishAsync(
                new ReleasePublishingRequest("0.1.0", string.Empty, IsPrerelease: false),
                progress: null,
                CancellationToken.None));

        Assert.Equal(501, exception.ReleaseId);
        Assert.Equal("v0.1.0", exception.Tag);
        Assert.IsType<OperationCanceledException>(exception.InnerException);
        Assert.Equal(
            ["get-file:distribution/update.json:main", "create-release", "upload:501"],
            github.Calls);
    }

    private static ReleasePublishingService CreateService(
        string root,
        FakeGitHubRepositoryClient github,
        IPublishScriptRunner runner,
        string privateKey) => new(
        root,
        new ReleaseManagerConfiguration(
            RepositoryCoordinates.Parse("owner/repo"),
            "secret",
            "main",
            ["https://mirror.example/"],
            Path.Combine(root, "unused.pem")),
        github,
        runner,
        new ReleaseArtifactBuilder(),
        new InMemorySigningKeyProvider(privateKey),
        new FixedTimeProvider(TestNow));

    private static GitHubRepositoryFile CreateRemoteManifest(
        string privateKey,
        string version,
        long sequence,
        string sha,
        DateTimeOffset? expiresAtUtc = null)
    {
        var manifest = SignedUpdateManifest.Create(
            new UpdateManifestPayload(
                sequence,
                expiresAtUtc.HasValue
                    ? TestNow.AddDays(-2)
                    : TestNow.AddMinutes(-1),
                expiresAtUtc ?? TestNow.AddDays(365),
                version,
                new UpdatePackage(
                    "RightMenuCheck-current-win-x64.zip",
                    123,
                    new string('A', 64),
                    "https://github.com/owner/repo/releases/download/current/package.zip",
                    []),
                "Current release",
                "https://github.com/owner/repo/releases/tag/current"),
            privateKey);
        return new GitHubRepositoryFile(
            ReleasePublishingService.UpdateManifestPath,
            sha,
            System.Text.Encoding.UTF8.GetBytes(DistributionJson.Serialize(manifest)));
    }

    private static SigningKeys CreateKeys()
    {
        using var algorithm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new SigningKeys(
            algorithm.ExportPkcs8PrivateKeyPem(),
            algorithm.ExportSubjectPublicKeyInfoPem());
    }

    private sealed record SigningKeys(string PrivateKey, string PublicKey);

    private sealed class FakePublishScriptRunner : IPublishScriptRunner
    {
        private readonly string _actualVersion;

        public FakePublishScriptRunner(string actualVersion)
        {
            _actualVersion = actualVersion;
        }

        public bool SupportsVersionArgument => true;

        public int InvocationCount { get; private set; }

        public Task<PublishScriptResult> RunAsync(
            PublishScriptRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            Directory.CreateDirectory(request.ExpectedOutputDirectory);
            File.WriteAllBytes(
                Path.Combine(request.ExpectedOutputDirectory, "RightMenuCheck.App.exe"),
                [1, 2, 3]);
            File.WriteAllText(
                Path.Combine(request.ExpectedOutputDirectory, "build-info.json"),
                JsonSerializer.Serialize(new { version = _actualVersion }));
            return Task.FromResult(new PublishScriptResult(
                0,
                request.ExpectedOutputDirectory,
                VersionArgumentApplied: true,
                string.Empty,
                string.Empty));
        }
    }
}
