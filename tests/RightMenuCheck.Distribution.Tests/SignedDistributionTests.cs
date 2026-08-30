using System.Globalization;
using System.Security.Cryptography;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.Distribution.Tests;

public sealed class SignedDistributionTests
{
    [Fact]
    public void SignedUpdateRoundTripsAndRequiresNewerRelease()
    {
        var keys = CreateKeys();
        var manifest = SignedUpdateManifest.Create(CreateUpdatePayload("1.2.0"), keys.PrivateKey);
        var json = DistributionJson.Serialize(manifest, writeIndented: true);
        var restored = DistributionJson.Deserialize<SignedUpdateManifest>(json);

        var decision = UpdatePolicyEvaluator.Evaluate(
            SemanticVersion.Parse("1.1.9"),
            restored,
            keys.PublicKey,
            restored.Payload.IssuedAtUtc.AddHours(1));

        Assert.True(restored.HasValidSignature(keys.PublicKey));
        Assert.Equal(UpdateDecisionKind.Required, decision.Kind);
        Assert.Equal(SemanticVersion.Parse("1.2.0"), decision.TargetVersion);
    }

    [Fact]
    public void UpdateAtSameOrOlderVersionDoesNotRequestInstall()
    {
        var keys = CreateKeys();
        var manifest = SignedUpdateManifest.Create(CreateUpdatePayload("1.2.0"), keys.PrivateKey);

        var decision = UpdatePolicyEvaluator.Evaluate(
            SemanticVersion.Parse("1.2.0"),
            manifest,
            keys.PublicKey,
            manifest.Payload.IssuedAtUtc.AddHours(1));

        Assert.Equal(UpdateDecisionKind.Current, decision.Kind);
    }

    [Fact]
    public void ExpiredCurrentManifestRequiresVersionStateRetry()
    {
        var keys = CreateKeys();
        var manifest = SignedUpdateManifest.Create(CreateUpdatePayload("1.2.0"), keys.PrivateKey);

        var decision = UpdatePolicyEvaluator.Evaluate(
            SemanticVersion.Parse("1.2.0"),
            manifest,
            keys.PublicKey,
            manifest.Payload.ExpiresAtUtc.AddMinutes(1));

        Assert.Equal(UpdateDecisionKind.StaleManifest, decision.Kind);
    }

    [Fact]
    public void ExpiredNewerManifestRequiresVersionStateRetry()
    {
        var keys = CreateKeys();
        var manifest = SignedUpdateManifest.Create(CreateUpdatePayload("1.2.0"), keys.PrivateKey);

        var decision = UpdatePolicyEvaluator.Evaluate(
            SemanticVersion.Parse("1.1.0"),
            manifest,
            keys.PublicKey,
            manifest.Payload.ExpiresAtUtc.AddMinutes(1));

        Assert.Equal(UpdateDecisionKind.StaleManifest, decision.Kind);
    }

    [Fact]
    public void TamperingWithSignedPayloadInvalidatesUpdate()
    {
        var keys = CreateKeys();
        var manifest = SignedUpdateManifest.Create(CreateUpdatePayload("1.2.0"), keys.PrivateKey);
        var tampered = manifest with
        {
            Payload = manifest.Payload with { Version = "9.0.0" },
        };

        var decision = UpdatePolicyEvaluator.Evaluate(
            SemanticVersion.Parse("1.0.0"),
            tampered,
            keys.PublicKey,
            manifest.Payload.IssuedAtUtc.AddHours(1));

        Assert.False(tampered.HasValidSignature(keys.PublicKey));
        Assert.Equal(UpdateDecisionKind.InvalidManifest, decision.Kind);
    }

    [Fact]
    public void AnnouncementSelectionHonorsSignatureWindowVersionAndRevision()
    {
        var keys = CreateKeys();
        var now = DateTimeOffset.Parse("2026-08-31T00:00:00Z", CultureInfo.InvariantCulture);
        var feed = SignedAnnouncementFeed.Create(
            new AnnouncementFeedPayload(
                Sequence: 1,
                IssuedAtUtc: now.AddHours(-2),
                ExpiresAtUtc: now.AddDays(1),
                [
                    new AnnouncementMessage(
                        "active",
                        Revision: 2,
                        "Service notice",
                        "Maintenance begins tonight.",
                        AnnouncementKind.Maintenance,
                        now.AddHours(-1),
                        now.AddHours(1),
                        MinimumVersion: "1.0.0",
                        MaximumVersion: "2.0.0"),
                    new AnnouncementMessage(
                        "future",
                        Revision: 1,
                        "Future",
                        "Not active yet.",
                        AnnouncementKind.Information,
                        now.AddHours(1),
                        EndsAtUtc: null,
                        MinimumVersion: null,
                        MaximumVersion: null),
                ]),
            keys.PrivateKey);

        var pending = AnnouncementSelector.SelectPending(
            feed,
            keys.PublicKey,
            SemanticVersion.Parse("1.5.0"),
            now,
            new Dictionary<string, int> { ["active"] = 1 });
        var alreadyShown = AnnouncementSelector.SelectPending(
            feed,
            keys.PublicKey,
            SemanticVersion.Parse("1.5.0"),
            now,
            new Dictionary<string, int> { ["active"] = 2 });

        Assert.Equal("active", Assert.Single(pending).Id);
        Assert.Empty(alreadyShown);
    }

    [Fact]
    public void TamperedAnnouncementFeedIsIgnored()
    {
        var keys = CreateKeys();
        var now = DateTimeOffset.UtcNow;
        var feed = SignedAnnouncementFeed.Create(
            new AnnouncementFeedPayload(
                Sequence: 1,
                IssuedAtUtc: now.AddHours(-1),
                ExpiresAtUtc: now.AddDays(1),
                [
                    new AnnouncementMessage(
                        "notice",
                        1,
                        "Original",
                        "Original body",
                        AnnouncementKind.Information,
                        now.AddMinutes(-1),
                        null,
                        null,
                        null),
                ]),
            keys.PrivateKey);
        var tampered = feed with
        {
            Payload = feed.Payload with
            {
                Messages = [feed.Payload.Messages[0] with { Body = "Changed" }],
            },
        };

        var pending = AnnouncementSelector.SelectPending(
            tampered,
            keys.PublicKey,
            SemanticVersion.Parse("1.0.0"),
            now,
            new Dictionary<string, int>());

        Assert.Empty(pending);
    }

    private static UpdateManifestPayload CreateUpdatePayload(string version) => new(
        Sequence: 1,
        IssuedAtUtc: DateTimeOffset.Parse(
            "2026-08-31T00:00:00Z",
            CultureInfo.InvariantCulture),
        ExpiresAtUtc: DateTimeOffset.Parse(
            "2026-09-30T00:00:00Z",
            CultureInfo.InvariantCulture),
        version,
        new UpdatePackage(
            "RightMenuCheck-1.2.0-win-x64.zip",
            SizeBytes: 1024,
            Sha256: new string('A', 64),
            PrimaryUrl: "https://github.com/owner/repo/releases/download/v1.2.0/app.zip",
            MirrorUrls:
            [
                "https://ghfast.top/https://github.com/owner/repo/releases/download/v1.2.0/app.zip",
            ]),
        "Release notes",
        "https://github.com/owner/repo/releases/tag/v1.2.0");

    private static SigningKeys CreateKeys()
    {
        using var algorithm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new SigningKeys(
            algorithm.ExportPkcs8PrivateKeyPem(),
            algorithm.ExportSubjectPublicKeyInfoPem());
    }

    private sealed record SigningKeys(string PrivateKey, string PublicKey);
}
