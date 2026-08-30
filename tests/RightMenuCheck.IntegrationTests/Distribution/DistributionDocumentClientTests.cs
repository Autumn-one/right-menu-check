using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using RightMenuCheck.App.Services;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.IntegrationTests.Distribution;

public sealed class DistributionDocumentClientTests
{
    [Fact]
    public async Task FetchSkipsTamperedSourceAndFallsBackToVerifiedCache()
    {
        using var fixture = new TemporaryDirectory();
        var keys = CreateKeys();
        var valid = CreateManifest(keys.PrivateKey, version: "1.1.0");
        var tampered = valid with
        {
            Payload = valid.Payload with { Version = "9.0.0" },
        };
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://mirror.example/update.json"] = DistributionJson.Serialize(tampered),
            ["https://github.example/update.json"] = DistributionJson.Serialize(valid),
        };
        using var httpClient = new HttpClient(new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[request.RequestUri!.AbsoluteUri]),
            }));
        var client = new DistributionDocumentClient(httpClient, NullAppLogger.Instance);
        var cachePath = Path.Combine(fixture.Path, "update-cache.json");

        var fetched = await client.FetchVerifiedAsync<SignedUpdateManifest>(
            responses.Keys.ToArray(),
            cachePath,
            manifest => manifest.HasValidSignature(keys.PublicKey),
            manifest => manifest.Payload.Sequence,
            CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal("1.1.0", fetched.Payload.Version);
        Assert.True(File.Exists(cachePath));

        using var offlineClient = new HttpClient(new StubHandler(_ =>
            throw new HttpRequestException("Synthetic network failure.")));
        var cached = await new DistributionDocumentClient(offlineClient, NullAppLogger.Instance)
            .FetchVerifiedAsync<SignedUpdateManifest>(
                responses.Keys.ToArray(),
                cachePath,
                manifest => manifest.HasValidSignature(keys.PublicKey),
                manifest => manifest.Payload.Sequence,
                CancellationToken.None);

        Assert.NotNull(cached);
        Assert.Equal("1.1.0", cached.Payload.Version);
    }

    [Fact]
    public async Task PackageDownloadRejectsBadMirrorHashAndUsesPrimary()
    {
        using var fixture = new TemporaryDirectory();
        var expected = "signed package bytes"u8.ToArray();
        var wrong = "tampered package byt"u8.ToArray();
        Assert.Equal(expected.Length, wrong.Length);
        var package = new UpdatePackage(
            "update.zip",
            expected.Length,
            Convert.ToHexString(SHA256.HashData(expected)),
            "https://github.example/update.zip",
            ["https://mirror.example/update.zip"]);
        using var httpClient = new HttpClient(new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(
                    request.RequestUri!.Host == "mirror.example" ? wrong : expected),
            }));
        var progressValues = new List<double>();
        var client = new DistributionDocumentClient(httpClient, NullAppLogger.Instance);

        var downloaded = await client.DownloadPackageAsync(
            package,
            fixture.Path,
            new Progress<double>(progressValues.Add),
            CancellationToken.None);

        Assert.Equal(expected, File.ReadAllBytes(downloaded));
        Assert.DoesNotContain(
            Directory.GetFiles(fixture.Path),
            path => path.Contains(".partial-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LowerSequenceMirrorCannotReplaceHigherVerifiedCache()
    {
        using var fixture = new TemporaryDirectory();
        var keys = CreateKeys();
        var cached = CreateManifest(keys.PrivateKey, "1.5.0", sequence: 5);
        var replayed = CreateManifest(keys.PrivateKey, "9.0.0", sequence: 4);
        var cachePath = Path.Combine(fixture.Path, "update-cache.json");
        File.WriteAllText(cachePath, DistributionJson.Serialize(cached, writeIndented: true));
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(DistributionJson.Serialize(replayed)),
            }));
        var client = new DistributionDocumentClient(httpClient, NullAppLogger.Instance);

        var fetched = await client.FetchVerifiedAsync<SignedUpdateManifest>(
            ["https://mirror.example/update.json"],
            cachePath,
            manifest => manifest.HasValidSignature(keys.PublicKey),
            manifest => manifest.Payload.Sequence,
            CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(5, fetched.Payload.Sequence);
        Assert.Equal("1.5.0", fetched.Payload.Version);
    }

    private static SignedUpdateManifest CreateManifest(
        string privateKey,
        string version,
        long sequence = 1) =>
        SignedUpdateManifest.Create(
            new UpdateManifestPayload(
                Sequence: sequence,
                IssuedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddDays(30),
                version,
                new UpdatePackage(
                    "update.zip",
                    10,
                    new string('A', 64),
                    "https://github.example/update.zip",
                    ["https://mirror.example/update.zip"]),
                "Notes",
                "https://github.example/release"),
            privateKey);

    private static SigningKeys CreateKeys()
    {
        using var algorithm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new SigningKeys(
            algorithm.ExportPkcs8PrivateKeyPem(),
            algorithm.ExportSubjectPublicKeyInfoPem());
    }

    private sealed record SigningKeys(string PrivateKey, string PublicKey);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RightMenuCheck-Distribution-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
