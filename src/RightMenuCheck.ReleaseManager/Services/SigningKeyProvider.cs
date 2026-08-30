using System.Security.Cryptography;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.ReleaseManager.Services;

public interface IDistributionSigningKeyProvider
{
    string ReadPrivateKey();
}

public sealed class FileDistributionSigningKeyProvider : IDistributionSigningKeyProvider
{
    private const long MaximumKeyFileBytes = 64 * 1024;
    private const string ValidationChallenge = "RightMenuCheck distribution key validation v1";
    private readonly Lazy<string> _validatedPrivateKey;
    private readonly string _privateKeyPath;
    private readonly string _publicKeyPath;

    public FileDistributionSigningKeyProvider(string privateKeyPath, string publicKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPath);
        _privateKeyPath = Path.GetFullPath(privateKeyPath);
        _publicKeyPath = Path.GetFullPath(publicKeyPath);
        _validatedPrivateKey = new Lazy<string>(
            ReadAndValidate,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string ReadPrivateKey() => _validatedPrivateKey.Value;

    private string ReadAndValidate()
    {
        var privateKey = ReadKeyFile(_privateKeyPath, "分发签名私钥");
        var publicKey = ReadKeyFile(_publicKeyPath, "客户端分发公钥");
        try
        {
            var signature = DistributionSignature.Sign(ValidationChallenge, privateKey);
            if (!DistributionSignature.Verify(ValidationChallenge, signature, publicKey))
            {
                throw new InvalidDataException("分发签名私钥与客户端内置公钥不匹配。");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new InvalidDataException("分发签名密钥格式无效。", exception);
        }

        return privateKey;
    }

    private static string ReadKeyFile(string path, string description)
    {
        var information = new FileInfo(path);
        if (!information.Exists)
        {
            throw new FileNotFoundException($"未找到{description}。", path);
        }

        if (information.Length is <= 0 or > MaximumKeyFileBytes)
        {
            throw new InvalidDataException($"{description}文件大小无效。");
        }

        return File.ReadAllText(path);
    }
}

public sealed class InMemorySigningKeyProvider : IDistributionSigningKeyProvider
{
    private readonly string _privateKey;

    public InMemorySigningKeyProvider(string privateKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKey);
        _privateKey = privateKey;
    }

    public string ReadPrivateKey() => _privateKey;
}
