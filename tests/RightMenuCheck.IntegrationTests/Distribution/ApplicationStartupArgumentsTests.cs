using RightMenuCheck.App.Services;

namespace RightMenuCheck.IntegrationTests.Distribution;

public sealed class ApplicationStartupArgumentsTests
{
    [Fact]
    public void ParsesHealthAndRollbackArgumentsWhileIgnoringUnrelatedArguments()
    {
        var token = Guid.NewGuid().ToString("N");
        const string pipeName = "RightMenuCheck.Update.Health.fixture";

        var parsed = ApplicationStartupArguments.Parse(
            [
                "--other",
                "value",
                "--update-health-pipe",
                pipeName,
                "--update-health-token",
                token,
                "--update-rollback",
            ]);

        Assert.Equal(pipeName, parsed.UpdateHealthPipeName);
        Assert.Equal(token, parsed.UpdateHealthToken);
        Assert.True(parsed.UpdateRolledBack);
    }

    [Theory]
    [InlineData("bad-token")]
    [InlineData("")]
    public void RejectsInvalidHealthToken(string token)
    {
        Assert.Throws<ArgumentException>(() => ApplicationStartupArguments.Parse(
            ["--update-health-pipe", "fixture.pipe", "--update-health-token", token]));
    }

    [Fact]
    public void RejectsDuplicateHealthTokens()
    {
        var token = Guid.NewGuid().ToString("N");

        Assert.Throws<ArgumentException>(() => ApplicationStartupArguments.Parse(
            [
                "--update-health-pipe",
                "fixture.pipe",
                "--update-health-token",
                token,
                "--update-health-token",
                token,
            ]));
    }

    [Fact]
    public void RejectsIncompleteHealthArguments()
    {
        var token = Guid.NewGuid().ToString("N");

        Assert.Throws<ArgumentException>(() => ApplicationStartupArguments.Parse(
            ["--update-health-token", token]));
        Assert.Throws<ArgumentException>(() => ApplicationStartupArguments.Parse(
            ["--update-health-pipe", "fixture.pipe"]));
    }
}
