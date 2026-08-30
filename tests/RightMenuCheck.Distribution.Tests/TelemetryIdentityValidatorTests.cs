using RightMenuCheck.Distribution;

namespace RightMenuCheck.Distribution.Tests;

public sealed class TelemetryIdentityValidatorTests
{
    [Fact]
    public void AcceptsHashedMachineAndCompactGuidSessionIdentifiers()
    {
        Assert.True(TelemetryIdentityValidator.IsValidMachineId(new string('a', 64)));
        Assert.True(TelemetryIdentityValidator.IsValidSessionId(Guid.NewGuid().ToString("N")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void RejectsInvalidMachineIdentifiers(string value)
    {
        Assert.False(TelemetryIdentityValidator.IsValidMachineId(value));
    }
}
