using RightMenuCheck.ReleaseManager.Publishing;
using RightMenuCheck.ReleaseManager.Tests.TestSupport;

namespace RightMenuCheck.ReleaseManager.Tests.Publishing;

public sealed class ReleaseArtifactBuilderTests
{
    [Theory]
    [InlineData("github-conf.json")]
    [InlineData("private-signing.pem")]
    [InlineData("certificate.pfx")]
    [InlineData(".env")]
    public async Task SensitiveFileStopsPackagingBeforeArchiveCreation(string sensitiveFileName)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "publish");
        var destination = Path.Combine(directory.Path, "releases");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "RightMenuCheck.App.exe"), "binary");
        File.WriteAllText(Path.Combine(source, sensitiveFileName), "do-not-disclose-token");
        var builder = new ReleaseArtifactBuilder();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            builder.BuildAsync(source, destination, "1.0.0", CancellationToken.None));

        Assert.Contains("敏感文件", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-disclose-token", exception.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task SecretsDirectoryStopsPackagingBeforeArchiveCreation()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "publish");
        var destination = Path.Combine(directory.Path, "releases");
        Directory.CreateDirectory(Path.Combine(source, ".secrets"));
        File.WriteAllText(
            Path.Combine(source, ".secrets", "signing.bin"),
            "do-not-disclose-token");
        var builder = new ReleaseArtifactBuilder();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            builder.BuildAsync(source, destination, "1.0.0", CancellationToken.None));

        Assert.Contains(".secrets", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }
}
