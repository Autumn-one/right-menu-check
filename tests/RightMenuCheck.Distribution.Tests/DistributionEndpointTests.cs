using RightMenuCheck.Distribution;

namespace RightMenuCheck.Distribution.Tests;

public sealed class DistributionEndpointTests
{
    [Fact]
    public void RawContentCandidatesPreferMirrorsThenGitHub()
    {
        var candidates = DistributionEndpoints.BuildRawContentCandidates(
            RepositoryCoordinates.Parse("Autumn-one/right-menu-check"),
            "main",
            "distribution/update.json");

        Assert.Equal(3, candidates.Count);
        Assert.StartsWith("https://ghfast.top/https://raw.githubusercontent.com/", candidates[0]);
        Assert.StartsWith("https://gh-proxy.com/https://raw.githubusercontent.com/", candidates[1]);
        Assert.Equal(
            "https://raw.githubusercontent.com/Autumn-one/right-menu-check/main/distribution/update.json",
            candidates[2]);
    }

    [Fact]
    public void ReleaseCandidatesEscapeTagAndAsset()
    {
        var candidates = DistributionEndpoints.BuildReleaseDownloadCandidates(
            RepositoryCoordinates.Parse("owner/repo"),
            "v1.2.0-rc.1",
            "RightMenuCheck win-x64.zip",
            ["https://mirror.example/"]);

        Assert.Equal(
            "https://mirror.example/https://github.com/owner/repo/releases/download/" +
            "v1.2.0-rc.1/RightMenuCheck%20win-x64.zip",
            candidates[0]);
        Assert.EndsWith(
            "/v1.2.0-rc.1/RightMenuCheck%20win-x64.zip",
            candidates[1],
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner/repo name")]
    public void RepositoryCoordinatesRejectInvalidValues(string value)
    {
        Assert.Throws<FormatException>(() => RepositoryCoordinates.Parse(value));
    }

    [Fact]
    public void EndpointsRejectInsecureMirror()
    {
        Assert.Throws<ArgumentException>(() => DistributionEndpoints.BuildRawContentCandidates(
            RepositoryCoordinates.Parse("owner/repo"),
            "main",
            "distribution/update.json",
            ["http://mirror.example/"]));
    }
}
