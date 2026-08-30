using System.Text.Json;
using RightMenuCheck.ReleaseManager.Configuration;
using RightMenuCheck.ReleaseManager.Tests.TestSupport;

namespace RightMenuCheck.ReleaseManager.Tests.Configuration;

public sealed class ReleaseManagerConfigurationTests
{
    private static readonly string[] ConfiguredMirrors = ["https://mirror.example/"];

    [Fact]
    public void LoadsRootConfigurationWithoutRenderingToken()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "scripts"));
        File.WriteAllText(Path.Combine(directory.Path, "scripts", "publish.ps1"), string.Empty);
        File.WriteAllText(
            Path.Combine(directory.Path, "github-conf.json"),
            JsonSerializer.Serialize(new
            {
                repo = "owner/right-menu-check",
                token = "github-secret-token",
                mirrors = ConfiguredMirrors,
                signingPrivateKeyPath = ".keys/signing.pem",
                branch = "stable",
            }));

        var configuration = ReleaseManagerConfiguration.Load(directory.Path);

        Assert.Equal("owner/right-menu-check", configuration.Repository.ToString());
        Assert.Equal("stable", configuration.DefaultBranch);
        Assert.Equal("https://mirror.example/", Assert.Single(configuration.MirrorPrefixes));
        Assert.Equal(
            Path.Combine(directory.Path, ".keys", "signing.pem"),
            configuration.SigningPrivateKeyPath);
        Assert.DoesNotContain("github-secret-token", configuration.ToString(), StringComparison.Ordinal);
        Assert.Equal(directory.Path, RepositoryRootLocator.FindFrom(Path.Combine(directory.Path, "scripts")));
    }

    [Fact]
    public void MissingOptionalValuesUseSafeDistributionDefaults()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "github-conf.json"),
            """
            { "repo": "owner/repo", "token": "token" }
            """);

        var configuration = ReleaseManagerConfiguration.Load(directory.Path);

        Assert.Equal("main", configuration.DefaultBranch);
        Assert.Equal(2, configuration.MirrorPrefixes.Count);
        Assert.Equal(
            Path.Combine(directory.Path, ".secrets", "update-signing-private.pem"),
            configuration.SigningPrivateKeyPath);
    }
}
