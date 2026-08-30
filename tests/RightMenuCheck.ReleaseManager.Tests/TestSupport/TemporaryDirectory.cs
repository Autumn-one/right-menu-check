namespace RightMenuCheck.ReleaseManager.Tests.TestSupport;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = Directory.CreateTempSubdirectory("rightmenu-release-manager-").FullName;
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
