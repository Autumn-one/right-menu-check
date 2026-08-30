using System.Security.Cryptography;
using RightMenuCheck.Updater;

namespace RightMenuCheck.Updater.Tests;

public sealed class EmbeddedPublicKeyTests
{
    private const string ExpectedFingerprint =
        "DBBA2438E473E3851F601256E37F70EE20E25459368D8229FCF84507B8EF812B";

    [Fact]
    public void UpdaterEmbedsExpectedDistributionPublicKey()
    {
        var assembly = typeof(UpdateInstaller).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith(".update-public-key.pem", StringComparison.Ordinal));
        using var stream = Assert.IsAssignableFrom<Stream>(
            assembly.GetManifestResourceStream(resourceName));
        using var reader = new StreamReader(stream);
        using var algorithm = ECDsa.Create();
        algorithm.ImportFromPem(reader.ReadToEnd());

        var fingerprint = Convert.ToHexString(
            SHA256.HashData(algorithm.ExportSubjectPublicKeyInfo()));

        Assert.Equal(ExpectedFingerprint, fingerprint);
    }
}
