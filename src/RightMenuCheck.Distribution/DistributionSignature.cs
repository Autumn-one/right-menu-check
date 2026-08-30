using System.Security.Cryptography;

namespace RightMenuCheck.Distribution;

public static class DistributionSignature
{
    public const string Algorithm = "ECDSA_P256_SHA256";

    public static string Sign<T>(T payload, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        using var algorithm = ECDsa.Create();
        algorithm.ImportFromPem(privateKeyPem);
        EnsureP256(algorithm);
        var signature = algorithm.SignData(
            DistributionJson.SerializeCanonical(payload),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return Convert.ToBase64String(signature);
    }

    public static bool Verify<T>(T payload, string signature, string publicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return false;
        }

        try
        {
            using var algorithm = ECDsa.Create();
            algorithm.ImportFromPem(publicKeyPem);
            EnsureP256(algorithm);
            return algorithm.VerifyData(
                DistributionJson.SerializeCanonical(payload),
                Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void EnsureP256(ECDsa algorithm)
    {
        if (algorithm.KeySize != 256)
        {
            throw new CryptographicException("Distribution signing requires an ECDSA P-256 key.");
        }
    }
}
