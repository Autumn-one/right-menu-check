using System.Security.Cryptography;
using RightMenuCheck.App.Services;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.IntegrationTests.Distribution;

public sealed class TelemetryEndpointDiscoveryServiceTests
{
    [Fact]
    public async Task SignedDiscoveryResolvesConfiguredHttpEndpoint()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKeyPem();
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;
        var document = SignedTelemetryEndpoint.Create(
            new TelemetryEndpointPayload(
                Sequence: 7,
                IssuedAtUtc: now,
                ExpiresAtUtc: now.AddDays(30),
                TelemetryProducts.RightMenuCheck,
                "http://43.159.148.243/"),
            privateKey);
        var documentClient = new FakeDocumentClient(document);
        var service = new TelemetryEndpointDiscoveryService(
            CreateConfiguration(publicKey),
            documentClient,
            NullAppLogger.Instance,
            overrideProvider: static () => null);

        var result = await service.ResolveAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("http://43.159.148.243/", result.BaseAddress.AbsoluteUri);
        Assert.True(result.AllowsInsecureHttp);
        Assert.Equal("SignedDiscovery", result.ResolutionKind);
        Assert.Equal(3, documentClient.Candidates.Length);
        Assert.Contains("Autumn-one/maidian", documentClient.Candidates[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsignedDiscoveryCannotEnableRemoteHttp()
    {
        using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var unrelatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = trustedKey.ExportSubjectPublicKeyInfoPem();
        var now = DateTimeOffset.UtcNow;
        var document = SignedTelemetryEndpoint.Create(
            new TelemetryEndpointPayload(
                Sequence: 1,
                IssuedAtUtc: now,
                ExpiresAtUtc: now.AddDays(30),
                TelemetryProducts.RightMenuCheck,
                "http://43.159.148.243/"),
            unrelatedKey.ExportPkcs8PrivateKeyPem());
        var service = new TelemetryEndpointDiscoveryService(
            CreateConfiguration(publicKey),
            new FakeDocumentClient(document),
            NullAppLogger.Instance,
            overrideProvider: static () => null);

        var result = await service.ResolveAsync(CancellationToken.None);

        Assert.Null(result);
    }

    private static EmbeddedDistributionConfiguration CreateConfiguration(string publicKey) => new(
        new AppDistributionSettings(
            AppDistributionSettings.CurrentSchemaVersion,
            "owner/right-menu-check",
            "main",
            "distribution/update.json",
            "distribution/messages.json",
            DistributionEndpoints.DefaultMirrorPrefixes,
            TelemetryBaseUrl: null,
            TelemetryDiscovery: new TelemetryDiscoverySettings(
                "Autumn-one/maidian",
                "main",
                "apps/rightmenucheck.json",
                TelemetryProducts.RightMenuCheck)),
        publicKey);

    private sealed class FakeDocumentClient(SignedTelemetryEndpoint document)
        : IDistributionDocumentClient
    {
        public string[] Candidates { get; private set; } = [];

        public Task<T?> FetchVerifiedAsync<T>(
            IReadOnlyList<string> candidates,
            string cachePath,
            Func<T, bool> validator,
            Func<T, long> sequenceSelector,
            CancellationToken cancellationToken)
            where T : class
        {
            _ = cachePath;
            _ = sequenceSelector;
            cancellationToken.ThrowIfCancellationRequested();
            Candidates = candidates.ToArray();
            var candidate = (T)(object)document;
            return Task.FromResult(validator(candidate) ? candidate : null);
        }

        public Task<string> DownloadPackageAsync(
            UpdatePackage package,
            string destinationDirectory,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Telemetry discovery does not download update packages.");
    }
}
