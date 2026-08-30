using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using RightMenuCheck.Windows.Security;

namespace RightMenuCheck.App.Services;

public interface IUpdaterLauncher
{
    void Launch(string updaterPath, string requestPath);
}

public sealed class SystemUpdaterLauncher : IUpdaterLauncher
{
    public void Launch(string updaterPath, string requestPath)
    {
        ProcessElevationPolicy.ThrowIfElevated("Updater launch");
        var startInfo = new ProcessStartInfo(Path.GetFullPath(updaterPath))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(updaterPath))!,
        };
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(Path.GetFullPath(requestPath));
        try
        {
            _ = Process.Start(startInfo) ??
                throw new InvalidOperationException("Updater process did not start.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Updater process could not be started.", exception);
        }
    }
}
