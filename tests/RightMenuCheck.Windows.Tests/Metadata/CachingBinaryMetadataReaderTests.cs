using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Metadata;

namespace RightMenuCheck.Windows.Tests.Metadata;

public sealed class CachingBinaryMetadataReaderTests
{
    [Fact]
    public void ReadCachesNormalizedPathWithCaseInsensitiveIdentity()
    {
        var inner = new CountingBinaryReader();
        var reader = new CachingBinaryMetadataReader(inner);
        var firstPath = "C:\\Handlers\\Shell.dll";
        var secondPath = "c:\\handlers\\.\\SHELL.dll";

        var first = reader.Read(firstPath);
        var second = reader.Read(secondPath);

        Assert.Same(first, second);
        Assert.Equal(1, inner.ReadCount);
    }

    private sealed class CountingBinaryReader : IBinaryMetadataReader
    {
        public int ReadCount { get; private set; }

        public BinaryFileMetadata Read(string filePath)
        {
            ReadCount++;
            return new BinaryFileMetadata(
                filePath,
                Exists: true,
                Size: 1,
                DateTimeOffset.UnixEpoch,
                Sha256: null,
                BinaryArchitectureKind.X64,
                IsManaged: false,
                FileVersion: "1.0.0.0",
                ProductVersion: "1.0.0.0",
                ProductName: null,
                Description: null,
                CompanyName: null,
                new AuthenticodeSignatureMetadata(
                    SignatureVerificationStatus.Unknown,
                    PublisherName: null,
                    Subject: null,
                    Issuer: null,
                    Thumbprint: null,
                    ValidFrom: null,
                    ValidTo: null,
                    TrustErrorCode: null),
                Issues: []);
        }
    }
}
