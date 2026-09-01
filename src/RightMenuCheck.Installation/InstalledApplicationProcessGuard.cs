using System.ComponentModel;
using System.Diagnostics;

namespace RightMenuCheck.Installation;

public static class InstalledApplicationProcessGuard
{
    public static async Task EnsureStoppedAsync(
        string applicationPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var expectedPath = Path.GetFullPath(applicationPath);
        foreach (var process in Process.GetProcessesByName("RightMenuCheck.App"))
        {
            using (process)
            {
                if (!TryGetProcessPath(process, out var path) ||
                    !path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _ = process.CloseMainWindow();
                try
                {
                    await process.WaitForExitAsync(cancellationToken)
                        .WaitAsync(timeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    throw new InvalidOperationException(
                        "请先关闭正在运行的 RightMenuCheck，再继续。",
                        exception);
                }
            }
        }
    }

    private static bool TryGetProcessPath(Process process, out string path)
    {
        try
        {
            path = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
            return path.Length > 0;
        }
        catch (Exception exception) when (exception is
                                           InvalidOperationException or
                                           Win32Exception or
                                           NotSupportedException)
        {
            path = string.Empty;
            return false;
        }
    }
}
