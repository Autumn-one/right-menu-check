using RightMenuCheck.Distribution;

namespace RightMenuCheck.Distribution.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("0.1.0")]
    [InlineData("1.2.3-alpha.1")]
    [InlineData("10.20.30-rc.2+build.8")]
    public void ParseRoundTripsValidVersions(string value)
    {
        var parsed = SemanticVersion.Parse(value);

        Assert.Equal(value, parsed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+bad space")]
    public void TryParseRejectsInvalidVersions(string value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.9.9", "2.0.0")]
    public void ComparisonFollowsSemanticVersionPrecedence(string lower, string higher)
    {
        Assert.True(SemanticVersion.Parse(lower) < SemanticVersion.Parse(higher));
    }

    [Fact]
    public void BuildMetadataDoesNotAffectPrecedence()
    {
        var left = SemanticVersion.Parse("1.2.3+first");
        var right = SemanticVersion.Parse("1.2.3+second");

        Assert.Equal(0, left.CompareTo(right));
    }
}
