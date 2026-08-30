using System.IO;

namespace RightMenuCheck.App.Services;

public interface IApplicationInstallContext
{
    int ProcessId { get; }

    string ApplicationPath { get; }

    string UpdaterSourcePath { get; }

    string UpdateRoot { get; }

    string TargetInstallDirectory { get; }
}

public sealed class SystemApplicationInstallContext : IApplicationInstallContext
{
    public int ProcessId => Environment.ProcessId;

    public string ApplicationPath => Environment.ProcessPath ??
                                     throw new InvalidOperationException(
                                         "Application path is unavailable.");

    public string UpdaterSourcePath => Path.Combine(
        AppContext.BaseDirectory,
        "helpers",
        "updater",
        "RightMenuCheck.Updater.exe");

    public string UpdateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RightMenuCheck",
        "Updates");

    public string TargetInstallDirectory
    {
        get
        {
            var installDirectory = Path.GetDirectoryName(Path.GetFullPath(ApplicationPath)) ??
                                   throw new InvalidOperationException(
                                       "Install directory is unavailable.");
            var installParent = Directory.GetParent(installDirectory)?.FullName ??
                                throw new InvalidOperationException(
                                    "Install directory cannot be a root directory.");
            return CanWriteDirectory(installParent)
                ? installDirectory
                : RightMenuCheck.Distribution.UpdateInstallLocations
                    .GetPerUserInstallDirectory();
        }
    }

    private static bool CanWriteDirectory(string directory)
    {
        var probe = Path.Combine(directory, $".rightmenucheck-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probe,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            TryDelete(probe);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
