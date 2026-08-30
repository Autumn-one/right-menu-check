using RightMenuCheck.Distribution;

namespace RightMenuCheck.Distribution.Tests;

public sealed class TelemetryIdentityValidatorTests
{
    [Fact]
    public void AcceptsHashedMachineAndCompactGuidSessionIdentifiers()
    {
        Assert.True(TelemetryIdentityValidator.IsValidMachineId(new string('a', 64)));
        Assert.True(TelemetryIdentityValidator.IsValidSessionId(Guid.NewGuid().ToString("N")));
        Assert.True(TelemetryIdentityValidator.IsValidSessionToken(new string('A', 43)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void RejectsInvalidMachineIdentifiers(string value)
    {
        Assert.False(TelemetryIdentityValidator.IsValidMachineId(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void RejectsInvalidSessionTokens(string value)
    {
        Assert.False(TelemetryIdentityValidator.IsValidSessionToken(value));
    }
}
