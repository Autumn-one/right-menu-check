using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Probe;

namespace RightMenuCheck.Windows.Tests.Probe;

public sealed class ProbeWorkerSelectorTests
{
    private readonly ProbeWorkerSelector _selector = new(new ProbeWorkerPaths(
        "C:\\Workers\\x64\\worker.exe",
        "C:\\Workers\\x86\\worker.exe",
        "C:\\Workers\\arm64\\worker.exe"));

    [Theory]
    [InlineData(BinaryArchitectureKind.X64, RegistryViewKind.Registry64, "X64")]
    [InlineData(BinaryArchitectureKind.X86, RegistryViewKind.Registry64, "X86")]
    [InlineData(BinaryArchitectureKind.Arm64, RegistryViewKind.Registry64, "Arm64")]
    [InlineData(BinaryArchitectureKind.AnyCpu, RegistryViewKind.Registry32, "X86")]
    [InlineData(BinaryArchitectureKind.AnyCpu, RegistryViewKind.Registry64, "X64")]
    [InlineData(BinaryArchitectureKind.Unknown, RegistryViewKind.Registry32, "X86")]
    public void SelectUsesBinaryArchitectureThenRegistryViewFallback(
        BinaryArchitectureKind architecture,
        RegistryViewKind registryView,
        string expected)
    {
        var result = _selector.Select(architecture, registryView);

        Assert.True(result.IsSupported);
        Assert.Equal(expected, result.Architecture);
        Assert.NotNull(result.WorkerPath);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void SelectReportsMissingArm64WorkerWithoutFallingBackToX64()
    {
        var selector = new ProbeWorkerSelector(new ProbeWorkerPaths(
            "C:\\Workers\\x64\\worker.exe",
            "C:\\Workers\\x86\\worker.exe",
            Arm64WorkerPath: null));

        var result = selector.Select(
            BinaryArchitectureKind.Arm64,
            RegistryViewKind.Registry64);

        Assert.False(result.IsSupported);
        Assert.Null(result.WorkerPath);
        Assert.Equal("Arm64", result.Architecture);
        Assert.NotNull(result.Reason);
    }
}
