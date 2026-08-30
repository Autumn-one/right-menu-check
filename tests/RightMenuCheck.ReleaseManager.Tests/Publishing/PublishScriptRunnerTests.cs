using RightMenuCheck.Distribution;
using RightMenuCheck.ReleaseManager.Publishing;
using RightMenuCheck.ReleaseManager.Tests.TestSupport;

namespace RightMenuCheck.ReleaseManager.Tests.Publishing;

public sealed class PublishScriptRunnerTests
{
    [Fact]
    public async Task PassesExactSemanticVersionAsSeparateScriptArgument()
    {
        using var directory = new TemporaryDirectory();
        var scriptsDirectory = Path.Combine(directory.Path, "scripts");
        var outputDirectory = Path.Combine(
            directory.Path,
            "artifacts",
            "publish",
            "RightMenuCheck");
        Directory.CreateDirectory(scriptsDirectory);
        var scriptPath = Path.Combine(scriptsDirectory, "publish.ps1");
        File.WriteAllText(
            scriptPath,
            """
            param(
                [Parameter(Mandatory)]
                [string]$Version
            )
            New-Item -ItemType Directory -Path '__OUTPUT__' -Force | Out-Null
            Set-Content -LiteralPath (Join-Path '__OUTPUT__' 'received-version.txt') -Value $Version
            """.Replace("__OUTPUT__", outputDirectory.Replace("'", "''"), StringComparison.Ordinal));
        var version = SemanticVersion.Parse("1.2.3-rc.1+build.7");
        var runner = new PowerShellPublishScriptRunner();

        var result = await runner.RunAsync(
            new PublishScriptRequest(
                directory.Path,
                version,
                scriptPath,
                outputDirectory),
            CancellationToken.None);

        Assert.True(runner.SupportsVersionArgument);
        Assert.True(result.VersionArgumentApplied);
        Assert.Equal(
            version.ToString(),
            File.ReadAllText(Path.Combine(outputDirectory, "received-version.txt")).Trim());
    }
}
