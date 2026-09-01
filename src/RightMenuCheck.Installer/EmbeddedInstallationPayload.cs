using System.Reflection;
using RightMenuCheck.Installation;

namespace RightMenuCheck.Installer;

internal sealed class EmbeddedInstallationPayload : IInstallationPayloadSource
{
    private const string PackageResourceName = "RightMenuCheck.Payload.zip";
    private const string UninstallerResourceName = "RightMenuCheck.Uninstaller.exe";
    private readonly Assembly _assembly = Assembly.GetExecutingAssembly();

    public EmbeddedInstallationPayload()
    {
        ExpectedVersion = ReadMetadata("RightMenuCheck.PayloadVersion");
        ExpectedPackageSha256 = ReadMetadata("RightMenuCheck.PayloadSha256");
        using var package = OpenResource(PackageResourceName);
        using var uninstaller = OpenResource(UninstallerResourceName);
        if (package.Length <= 0 || uninstaller.Length <= 0)
        {
            throw new InvalidDataException("Setup payload resources are empty.");
        }
    }

    public string ExpectedVersion { get; }

    public string ExpectedPackageSha256 { get; }

    public Stream OpenApplicationPackage() => OpenResource(PackageResourceName);

    public Stream OpenUninstaller() => OpenResource(UninstallerResourceName);

    private string ReadMetadata(string key)
    {
        var value = _assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key.Equals(key, StringComparison.Ordinal))?
            .Value;
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Setup metadata is missing: {key}")
            : value.Trim();
    }

    private Stream OpenResource(string name) =>
        _assembly.GetManifestResourceStream(name) ??
        throw new InvalidDataException($"Setup resource is missing: {name}");
}
