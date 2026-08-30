using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Metadata;

namespace RightMenuCheck.Windows.Tests.Metadata;

public sealed class BinaryMetadataReaderTests
{
    [Fact]
    public void ReadReturnsManagedArchitectureHashAndVersionForTestAssembly()
    {
        var reader = new BinaryMetadataReader();
        var assemblyPath = typeof(BinaryMetadataReader).Assembly.Location;

        var result = reader.Read(assemblyPath);

        Assert.True(result.Exists);
        Assert.True(result.IsManaged);
        Assert.Equal(BinaryArchitectureKind.AnyCpu, result.Architecture);
        Assert.NotNull(result.Size);
        Assert.True(result.Size > 0);
        Assert.NotNull(result.LastWriteTime);
        Assert.NotNull(result.FileVersion);
        Assert.Matches("^[0-9A-F]{64}$", result.Sha256);
        Assert.Equal(SignatureVerificationStatus.NoSignature, result.Signature.Status);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ReadReturnsEvidenceInsteadOfThrowingForMissingFile()
    {
        var reader = new BinaryMetadataReader();
        var missingPath = Path.Combine(
            AppContext.BaseDirectory,
            $"missing-{Guid.NewGuid():N}.dll");

        var result = reader.Read(missingPath);

        Assert.False(result.Exists);
        Assert.Null(result.Sha256);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("FileNotFound", issue.ErrorType);
    }

    [Fact]
    public void ReadReturnsEvidenceInsteadOfThrowingForInvalidPath()
    {
        var reader = new BinaryMetadataReader();

        var result = reader.Read("invalid\0path.dll");

        Assert.False(result.Exists);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("NormalizeBinaryPath", issue.Operation);
        Assert.Equal(nameof(ArgumentException), issue.ErrorType);
    }
}
