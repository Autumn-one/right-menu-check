using System.Security.Cryptography;
using System.Text;
using RightMenuCheck.Distribution;
using RightMenuCheck.ReleaseManager.GitHub;
using RightMenuCheck.ReleaseManager.Publishing;
using RightMenuCheck.ReleaseManager.Services;
using RightMenuCheck.ReleaseManager.Tests.TestSupport;

namespace RightMenuCheck.ReleaseManager.Tests.Services;

public sealed class ReleaseAdministrationServiceTests
{
    [Fact]
    public async Task ConfirmationImpactCarriesExactReleaseIdAndTagIntoDeletion()
    {
        var github = new FakeGitHubRepositoryClient();
        var service = new ReleaseAdministrationService(github, "main", "unused-public-key");
        var release = new GitHubRelease(
            918273,
            "v2.4.1",
            "RightMenuCheck 2.4.1",
            string.Empty,
            IsDraft: false,
            IsPrerelease: false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "https://github.com/owner/repo/releases/tag/v2.4.1",
            [
                new GitHubReleaseAsset(
                    1,
                    "package.zip",
                    2048,
                    "https://example.test/package.zip",
                    "application/zip",
                    0),
            ]);

        var impact = ReleaseAdministrationService.PreviewDeletion(release, deleteTag: true);
        var result = await service.DeleteAsync(impact, CancellationToken.None);

        Assert.Contains("Release ID：918273", impact.CreatePreview(), StringComparison.Ordinal);
        Assert.Equal(918273, result.ReleaseId);
        Assert.Equal("v2.4.1", result.ExactTag);
        Assert.Equal(
            [
                $"get-file:{ReleasePublishingService.UpdateManifestPath}:main",
                "delete-release:918273",
                "delete-tag:v2.4.1",
            ],
            github.Calls);
        Assert.Equal(918273, github.DeletedReleaseId);
        Assert.Equal("v2.4.1", github.DeletedTag);
    }

    [Fact]
    public async Task KeepingTagNeverCallsTagDeletion()
    {
        var github = new FakeGitHubRepositoryClient();
        var service = new ReleaseAdministrationService(github, "main", "unused-public-key");
        var release = new GitHubRelease(
            22,
            "v1.0.0",
            "Release",
            string.Empty,
            false,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "https://example.test/release",
            []);

        var result = await service.DeleteAsync(
            ReleaseAdministrationService.PreviewDeletion(release, deleteTag: false),
            CancellationToken.None);

        Assert.True(result.ReleaseDeleted);
        Assert.False(result.TagDeleted);
        Assert.Equal(
            [
                $"get-file:{ReleasePublishingService.UpdateManifestPath}:main",
                "delete-release:22",
            ],
            github.Calls);
    }

    [Fact]
    public async Task CurrentManifestReleaseIsRejectedBeforeRemoteDeletion()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKeyPem();
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var manifest = SignedUpdateManifest.Create(
            new UpdateManifestPayload(
                Sequence: 7,
                IssuedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddDays(30),
                Version: "2.4.1",
                new UpdatePackage(
                    "package.zip",
                    2048,
                    new string('A', 64),
                    "https://github.com/owner/repo/releases/download/v2.4.1/package.zip",
                    []),
                "Notes",
                "https://github.com/owner/repo/releases/tag/v2.4.1"),
            privateKey);
        var github = new FakeGitHubRepositoryClient
        {
            RepositoryFile = new GitHubRepositoryFile(
                ReleasePublishingService.UpdateManifestPath,
                "manifest-sha",
                Encoding.UTF8.GetBytes(DistributionJson.Serialize(manifest))),
        };
        var service = new ReleaseAdministrationService(github, "main", publicKey);
        var release = new GitHubRelease(
            918273,
            "v2.4.1",
            "RightMenuCheck 2.4.1",
            string.Empty,
            false,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            manifest.Payload.ReleasePageUrl,
            []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(
                ReleaseAdministrationService.PreviewDeletion(release, deleteTag: true),
                CancellationToken.None));

        Assert.Contains("当前更新清单", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            [$"get-file:{ReleasePublishingService.UpdateManifestPath}:main"],
            github.Calls);
        Assert.Null(github.DeletedReleaseId);
        Assert.Null(github.DeletedTag);
    }
}
