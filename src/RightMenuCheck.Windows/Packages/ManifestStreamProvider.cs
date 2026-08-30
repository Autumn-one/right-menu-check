namespace RightMenuCheck.Windows.Packages;

public interface IManifestStreamProvider
{
    Stream OpenRead(string manifestPath);
}

public sealed class PhysicalManifestStreamProvider : IManifestStreamProvider
{
    public Stream OpenRead(string manifestPath) => new FileStream(
        manifestPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 4096,
        FileOptions.SequentialScan);
}
