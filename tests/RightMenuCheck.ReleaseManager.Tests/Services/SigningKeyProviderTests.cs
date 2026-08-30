using System.Security.Cryptography;
using RightMenuCheck.ReleaseManager.Services;
using RightMenuCheck.ReleaseManager.Tests.TestSupport;

namespace RightMenuCheck.ReleaseManager.Tests.Services;

public sealed class SigningKeyProviderTests
{
    [Fact]
    public void MatchingPrivateAndPublicKeysAreAccepted()
    {
        using var temporary = new TemporaryDirectory();
        var (privateKey, publicKey) = CreateKeys();
        var privatePath = Path.Combine(temporary.Path, "private.pem");
        var publicPath = Path.Combine(temporary.Path, "public.pem");
        File.WriteAllText(privatePath, privateKey);
        File.WriteAllText(publicPath, publicKey);

        var provider = new FileDistributionSigningKeyProvider(privatePath, publicPath);

        Assert.Equal(privateKey, provider.ReadPrivateKey());
    }

    [Fact]
    public void MismatchedPrivateAndPublicKeysAreRejected()
    {
        using var temporary = new TemporaryDirectory();
        var (privateKey, _) = CreateKeys();
        var (_, unrelatedPublicKey) = CreateKeys();
        var privatePath = Path.Combine(temporary.Path, "private.pem");
        var publicPath = Path.Combine(temporary.Path, "public.pem");
        File.WriteAllText(privatePath, privateKey);
        File.WriteAllText(publicPath, unrelatedPublicKey);

        var provider = new FileDistributionSigningKeyProvider(privatePath, publicPath);

        var exception = Assert.Throws<InvalidDataException>(provider.ReadPrivateKey);
        Assert.Contains("不匹配", exception.Message, StringComparison.Ordinal);
    }

    private static (string PrivateKey, string PublicKey) CreateKeys()
    {
        using var algorithm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            algorithm.ExportPkcs8PrivateKeyPem(),
            algorithm.ExportSubjectPublicKeyInfoPem());
    }
}
