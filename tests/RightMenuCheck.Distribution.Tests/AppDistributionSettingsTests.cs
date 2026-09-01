using RightMenuCheck.Distribution;

namespace RightMenuCheck.Distribution.Tests;

public sealed class AppDistributionSettingsTests
{
    [Fact]
    public void SettingsBuildSignedContentCandidatesAndAllowLoopbackTelemetry()
    {
        var settings = new AppDistributionSettings(
            AppDistributionSettings.CurrentSchemaVersion,
            "Autumn-one/right-menu-check",
            "main",
            "distribution/update.json",
            "distribution/messages.json",
            DistributionEndpoints.DefaultMirrorPrefixes,
            "http://127.0.0.1:17789/",
            new TelemetryDiscoverySettings(
                "Autumn-one/maidian",
                "main",
                "apps/rightmenucheck.json",
                TelemetryProducts.RightMenuCheck));

        settings.Validate();

        Assert.Equal(3, settings.GetUpdateManifestCandidates().Count);
        Assert.EndsWith(
            "/distribution/messages.json",
            settings.GetAnnouncementCandidates()[2],
            StringComparison.Ordinal);
        Assert.Equal(3, settings.GetTelemetryEndpointCandidates().Count);
        Assert.Contains(
            "Autumn-one/maidian",
            settings.GetTelemetryEndpointCandidates()[0],
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://telemetry.example.com/")]
    [InlineData("file:///C:/telemetry")]
    public void SettingsRejectUnsafeTelemetryUrls(string telemetryUrl)
    {
        var settings = new AppDistributionSettings(
            AppDistributionSettings.CurrentSchemaVersion,
            "owner/repo",
            "main",
            "distribution/update.json",
            "distribution/messages.json",
            DistributionEndpoints.DefaultMirrorPrefixes,
            telemetryUrl);

        Assert.Throws<InvalidDataException>(settings.Validate);
    }
}
