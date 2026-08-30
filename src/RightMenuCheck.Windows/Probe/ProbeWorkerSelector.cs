using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;

namespace RightMenuCheck.Windows.Probe;

public sealed record ProbeWorkerPaths(
    string X64WorkerPath,
    string X86WorkerPath,
    string? Arm64WorkerPath);

public sealed record ProbeWorkerSelection(
    bool IsSupported,
    string? WorkerPath,
    string Architecture,
    string? Reason);

public sealed class ProbeWorkerSelector
{
    private readonly ProbeWorkerPaths _paths;

    public ProbeWorkerSelector(ProbeWorkerPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public ProbeWorkerSelection Select(
        BinaryArchitectureKind binaryArchitecture,
        RegistryViewKind registryView)
    {
        return binaryArchitecture switch
        {
            BinaryArchitectureKind.X86 or BinaryArchitectureKind.AnyCpuPrefer32Bit =>
                Supported(_paths.X86WorkerPath, "X86"),
            BinaryArchitectureKind.X64 => Supported(_paths.X64WorkerPath, "X64"),
            BinaryArchitectureKind.Arm64 when _paths.Arm64WorkerPath is { } arm64Path =>
                Supported(arm64Path, "Arm64"),
            BinaryArchitectureKind.Arm64 => Unsupported(
                "Arm64",
                "No ARM64 probe worker is available in this installation."),
            BinaryArchitectureKind.Arm => Unsupported(
                "Arm",
                "32-bit ARM Shell extensions are not supported by the available worker targets."),
            BinaryArchitectureKind.AnyCpu or BinaryArchitectureKind.Unknown =>
                registryView == RegistryViewKind.Registry32
                    ? Supported(_paths.X86WorkerPath, "X86")
                    : Supported(_paths.X64WorkerPath, "X64"),
            _ => Unsupported("Unknown", "The handler binary architecture is unsupported."),
        };
    }

    private static ProbeWorkerSelection Supported(string path, string architecture) =>
        new(IsSupported: true, path, architecture, Reason: null);

    private static ProbeWorkerSelection Unsupported(string architecture, string reason) =>
        new(IsSupported: false, WorkerPath: null, architecture, reason);
}
