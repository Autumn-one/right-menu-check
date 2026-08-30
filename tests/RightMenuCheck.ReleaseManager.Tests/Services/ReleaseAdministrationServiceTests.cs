using RightMenuCheck.ReleaseManager.GitHub;
using RightMenuCheck.ReleaseManager.Services;
using RightMenuCheck.ReleaseManager.Tests.TestSupport;

namespace RightMenuCheck.ReleaseManager.Tests.Services;

public sealed class ReleaseAdministrationServiceTests
{
    [Fact]
    public async Task ConfirmationImpactCarriesExactReleaseIdAndTagIntoDeletion()
    {
        var github = new FakeGitHubRepositoryClient();
        var service = new ReleaseAdministrationService(github);
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
            ["delete-release:918273", "delete-tag:v2.4.1"],
            github.Calls);
        Assert.Equal(918273, github.DeletedReleaseId);
        Assert.Equal("v2.4.1", github.DeletedTag);
    }

    [Fact]
    public async Task KeepingTagNeverCallsTagDeletion()
    {
        var github = new FakeGitHubRepositoryClient();
        var service = new ReleaseAdministrationService(github);
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
        Assert.Equal(["delete-release:22"], github.Calls);
    }
}
