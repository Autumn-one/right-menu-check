using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RightMenuCheck.Uninstaller;

internal static partial class Program
{
    private const int MaximumDeleteDepth = 64;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxIconInformation = 0x00000040;
    private const string ReadyEventPrefix = "Local\\RightMenuCheck.Uninstall.";
    private const uint TokenQuery = 0x0008;
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RightMenuCheck";

    [STAThread]
    public static int Main(string[] args)
    {
        var quiet = args.Contains("--quiet", StringComparer.Ordinal);
        try
        {
            ThrowIfElevated("RightMenuCheck uninstall");
            var paths = UninstallPaths.CreateSystem();
            paths.Validate();
            return args.Length > 0 && args[0].Equals("--worker", StringComparison.Ordinal)
                ? RunWorkerAsync(args, paths, quiet).GetAwaiter().GetResult()
                : LaunchWorker(args, paths, quiet);
        }
        catch (Exception exception) when (exception is
                                           ArgumentException or
                                           InvalidDataException or
                                           IOException or
                                           TimeoutException or
                                           UnauthorizedAccessException or
                                           InvalidOperationException or
                                           Win32Exception)
        {
            if (!quiet)
            {
                ShowMessage(
                    $"RightMenuCheck 卸载未完成：{exception.Message}",
                    "卸载失败",
                    MessageBoxIconError);
            }

            return 1;
        }
    }

    private static int LaunchWorker(string[] arguments, UninstallPaths paths, bool quiet)
    {
        if (arguments.Any(argument =>
                !argument.Equals("--quiet", StringComparison.Ordinal)))
        {
            throw new ArgumentException("Unsupported uninstaller argument.");
        }

        var executable = Path.GetFullPath(Environment.ProcessPath ??
                                          throw new InvalidOperationException(
                                              "Uninstaller path is unavailable."));
        if (!executable.Equals(
                Path.GetFullPath(paths.UninstallerPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Uninstaller must be launched from the registered installer cache.");
        }

        using var current = Process.GetCurrentProcess();
        var readyEventName = $"{ReadyEventPrefix}{Guid.NewGuid():N}";
        using var readyEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            readyEventName,
            out _);
        var workerDirectory = Path.Combine(
            Path.GetTempPath(),
            "RightMenuCheck",
            $"Uninstall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workerDirectory);
        var workerPath = Path.Combine(workerDirectory, "RightMenuCheck.Uninstaller.exe");
        File.Copy(executable, workerPath, overwrite: false);
        var startInfo = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = workerDirectory,
        };
        startInfo.ArgumentList.Add("--worker");
        startInfo.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(
            current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(readyEventName);
        if (quiet)
        {
            startInfo.ArgumentList.Add("--quiet");
        }

        using var worker = Process.Start(startInfo) ??
                           throw new InvalidOperationException(
                               "Unable to start the uninstall worker.");
        if (!readyEvent.WaitOne(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Uninstall worker did not take control in time.");
        }

        return 0;
    }

    private static async Task<int> RunWorkerAsync(
        string[] arguments,
        UninstallPaths paths,
        bool quiet)
    {
        if (arguments.Length is < 4 or > 5 ||
            !int.TryParse(
                arguments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentProcessId) ||
            !long.TryParse(
                arguments[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentStartTicks))
        {
            throw new ArgumentException("Uninstall worker identity is invalid.");
        }

        var readyEventName = arguments[3];
        if (readyEventName.Length != ReadyEventPrefix.Length + 32 ||
            !readyEventName.StartsWith(ReadyEventPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(readyEventName[^32..], "N", out _))
        {
            throw new ArgumentException("Uninstall worker ready endpoint is invalid.");
        }

        ValidateWorkerLocation();
        await WaitForParentAsync(
                parentProcessId,
                parentStartTicks,
                paths.UninstallerPath,
                readyEventName)
            .ConfigureAwait(false);
        await EnsureApplicationStoppedAsync(
                paths.ApplicationPath,
                TimeSpan.FromSeconds(15),
                CancellationToken.None)
            .ConfigureAwait(false);
        DeleteDirectory(paths.InstallDirectory);
        File.Delete(paths.StartMenuShortcutPath);
        File.Delete(paths.DesktopShortcutPath);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        DeleteDirectory(paths.InstallerCacheDirectory);
        if (!quiet)
        {
            ShowMessage(
                "RightMenuCheck 已从当前用户卸载。用户日志和备份数据已保留。",
                "卸载完成",
                MessageBoxIconInformation);
        }

        TryDeleteWorkerExecutable();
        return 0;
    }

    private static void ValidateWorkerLocation()
    {
        var executable = Path.GetFullPath(Environment.ProcessPath ??
                                          throw new InvalidOperationException(
                                              "Uninstall worker path is unavailable."));
        var expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "RightMenuCheck")));
        if (!executable.StartsWith(
                expectedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Uninstall worker is outside the temporary root.");
        }
    }

    private static async Task WaitForParentAsync(
        int processId,
        long expectedStartTicks,
        string expectedPath,
        string readyEventName)
    {
        using var process = Process.GetProcessById(processId);
        var actualStartTicks = process.StartTime.ToUniversalTime().Ticks;
        var actualPath = Path.GetFullPath(process.MainModule?.FileName ?? string.Empty);
        if (actualStartTicks != expectedStartTicks ||
            !actualPath.Equals(
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Uninstall parent process identity did not match.");
        }

        using var readyEvent = EventWaitHandle.OpenExisting(readyEventName);
        _ = readyEvent.Set();
        await process.WaitForExitAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30))
            .ConfigureAwait(false);
    }

    private static async Task EnsureApplicationStoppedAsync(
        string applicationPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
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

    private static void DeleteDirectory(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists)
        {
            return;
        }

        if (directory.Parent is null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "Refusing to recursively delete a root or reparse-point directory.");
        }

        DeleteContents(directory, depth: 0);
        directory.Delete(recursive: false);
    }

    private static void DeleteContents(DirectoryInfo directory, int depth)
    {
        if (depth >= MaximumDeleteDepth)
        {
            throw new InvalidDataException("Directory tree exceeds the supported depth.");
        }

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entry.Delete();
                continue;
            }

            if (entry is DirectoryInfo child)
            {
                DeleteContents(child, depth + 1);
                child.Delete(recursive: false);
                continue;
            }

            entry.Attributes = FileAttributes.Normal;
            entry.Delete();
        }
    }

    private static void TryDeleteWorkerExecutable()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        try
        {
            File.Delete(executable);
            Directory.Delete(Path.GetDirectoryName(executable)!, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void ThrowIfElevated(string operation)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (token)
        {
            var elevation = new TokenElevation();
            var size = Marshal.SizeOf<TokenElevation>();
            if (!GetTokenInformation(
                    token,
                    TokenInformationClass.TokenElevation,
                    ref elevation,
                    size,
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (elevation.TokenIsElevated != 0)
            {
                throw new InvalidOperationException(
                    $"{operation} must run as the current standard user, not as administrator.");
            }
        }
    }

    private static void ShowMessage(string message, string title, uint icon) =>
        _ = MessageBox(nint.Zero, message, title, icon);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(
        nint windowHandle,
        string text,
        string caption,
        uint type);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out SafeFileHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeFileHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        ref TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20,
    }

    private sealed record UninstallPaths(
        string InstallDirectory,
        string InstallerCacheDirectory,
        string UninstallerPath,
        string StartMenuShortcutPath,
        string DesktopShortcutPath)
    {
        public string ApplicationPath => Path.Combine(InstallDirectory, "RightMenuCheck.App.exe");

        public static UninstallPaths CreateSystem()
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            var cache = Path.Combine(localAppData, "RightMenuCheck", "Installer");
            return new UninstallPaths(
                Path.Combine(localAppData, "Programs", "RightMenuCheck"),
                cache,
                Path.Combine(cache, "RightMenuCheck.Uninstaller.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    "RightMenuCheck.lnk"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "RightMenuCheck.lnk"));
        }

        public void Validate()
        {
            var values = new[]
            {
                InstallDirectory,
                InstallerCacheDirectory,
                UninstallerPath,
                StartMenuShortcutPath,
                DesktopShortcutPath,
            };
            if (values.Any(static value =>
                    string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) ||
                Directory.GetParent(Path.GetFullPath(InstallDirectory)) is null ||
                Directory.GetParent(Path.GetFullPath(InstallerCacheDirectory)) is null ||
                !Path.GetDirectoryName(Path.GetFullPath(UninstallerPath))!.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(InstallerCacheDirectory)),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Uninstall paths are invalid.");
            }
        }
    }
}
