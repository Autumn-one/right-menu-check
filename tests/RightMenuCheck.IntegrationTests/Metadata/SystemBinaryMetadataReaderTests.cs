using System.Runtime.InteropServices;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Metadata;

namespace RightMenuCheck.IntegrationTests.Metadata;

public sealed class SystemBinaryMetadataReaderTests
{
    [Fact]
    public void ReadVerifiesArchitectureVersionHashAndSignatureOfSystemBinary()
    {
        var binaryPath = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        var reader = new BinaryMetadataReader();

        var result = reader.Read(binaryPath);

        Assert.True(result.Exists);
        Assert.False(result.IsManaged);
        Assert.Equal(GetExpectedArchitecture(), result.Architecture);
        Assert.NotNull(result.FileVersion);
        Assert.NotNull(result.CompanyName);
        Assert.Matches("^[0-9A-F]{64}$", result.Sha256);
        Assert.Equal(SignatureVerificationStatus.Valid, result.Signature.Status);
        Assert.Empty(result.Issues);
    }

    private static BinaryArchitectureKind GetExpectedArchitecture() =>
        RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => BinaryArchitectureKind.X86,
            Architecture.X64 => BinaryArchitectureKind.X64,
            Architecture.Arm => BinaryArchitectureKind.Arm,
            Architecture.Arm64 => BinaryArchitectureKind.Arm64,
            _ => BinaryArchitectureKind.Unknown,
        };
}
