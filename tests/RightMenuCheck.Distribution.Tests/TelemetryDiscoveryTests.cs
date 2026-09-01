using System.Security.Cryptography;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.Distribution.Tests;

public sealed class TelemetryDiscoveryTests
{
    [Fact]
    public void SignedHttpEndpointIsAvailableOnlyThroughVerifiedDiscovery()
    {
        var keys = CreateKeys();
        var now = DateTimeOffset.UtcNow;
        var document = CreateDocument(keys.PrivateKey, now, "http://43.159.148.243/");

        var decision = TelemetryEndpointPolicy.Evaluate(
            document,
            keys.PublicKey,
            TelemetryProducts.RightMenuCheck,
            now);

        Assert.Equal(TelemetryEndpointDecisionKind.Available, decision.Kind);
        Assert.Equal("http://43.159.148.243/", decision.BaseAddress?.AbsoluteUri);
        Assert.True(decision.AllowsInsecureHttp);
    }

    [Fact]
    public void TamperedOrWrongProductEndpointIsRejected()
    {
        var keys = CreateKeys();
        var now = DateTimeOffset.UtcNow;
        var document = CreateDocument(keys.PrivateKey, now, "https://telemetry.example.test/");
        var tampered = document with
        {
            Payload = document.Payload with { BaseUrl = "http://attacker.example/" },
        };

        Assert.Equal(
            TelemetryEndpointDecisionKind.Invalid,
            TelemetryEndpointPolicy.Evaluate(
                tampered,
                keys.PublicKey,
                TelemetryProducts.RightMenuCheck,
                now).Kind);
        Assert.Equal(
            TelemetryEndpointDecisionKind.Invalid,
            TelemetryEndpointPolicy.Evaluate(
                document,
                keys.PublicKey,
                "another-product",
                now).Kind);
    }

    [Fact]
    public void ExpiredEndpointIsStale()
    {
        var keys = CreateKeys();
        var now = DateTimeOffset.UtcNow;
        var payload = new TelemetryEndpointPayload(
            Sequence: 4,
            IssuedAtUtc: now.AddDays(-2),
            ExpiresAtUtc: now.AddDays(-1),
            TelemetryProducts.RightMenuCheck,
            "https://telemetry.example.test/");

        var decision = TelemetryEndpointPolicy.Evaluate(
            SignedTelemetryEndpoint.Create(payload, keys.PrivateKey),
            keys.PublicKey,
            TelemetryProducts.RightMenuCheck,
            now);

        Assert.Equal(TelemetryEndpointDecisionKind.Stale, decision.Kind);
    }

    private static SignedTelemetryEndpoint CreateDocument(
        string privateKey,
        DateTimeOffset now,
        string baseUrl) =>
        SignedTelemetryEndpoint.Create(
            new TelemetryEndpointPayload(
                Sequence: 1,
                IssuedAtUtc: now,
                ExpiresAtUtc: now.AddDays(30),
                TelemetryProducts.RightMenuCheck,
                baseUrl),
            privateKey);

    private static SigningKeys CreateKeys()
    {
        using var algorithm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new SigningKeys(
            algorithm.ExportPkcs8PrivateKeyPem(),
            algorithm.ExportSubjectPublicKeyInfoPem());
    }

    private sealed record SigningKeys(string PrivateKey, string PublicKey);
}
