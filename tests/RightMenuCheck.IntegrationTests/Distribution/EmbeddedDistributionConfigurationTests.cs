using System.Security.Cryptography;
using RightMenuCheck.App.Services;

namespace RightMenuCheck.IntegrationTests.Distribution;

public sealed class EmbeddedDistributionConfigurationTests
{
    [Fact]
    public void AppEmbedsValidatedRepositoryMirrorsAndExpectedPublicKey()
    {
        var configuration = EmbeddedDistributionConfigurationLoader.Load();
        using var algorithm = ECDsa.Create();
        algorithm.ImportFromPem(configuration.PublicKeyPem);

        var fingerprint = Convert.ToHexString(
            SHA256.HashData(algorithm.ExportSubjectPublicKeyInfo()));

        Assert.Equal("Autumn-one/right-menu-check", configuration.Settings.Repository);
        Assert.Equal(3, configuration.Settings.GetUpdateManifestCandidates().Count);
        Assert.Equal(
            "DBBA2438E473E3851F601256E37F70EE20E25459368D8229FCF84507B8EF812B",
            fingerprint);
    }
}
