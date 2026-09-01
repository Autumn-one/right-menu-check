using System.Diagnostics;
using System.IO;
using System.Windows;
using RightMenuCheck.Installation;

namespace RightMenuCheck.Installer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            ProcessElevationGuard.ThrowIfElevated("RightMenuCheck setup");
            TemporaryUninstallWorkerCleanup.TryCleanup();
            var arguments = InstallerArguments.Parse(e.Args);
            var payload = new EmbeddedInstallationPayload();
            var paths = InstallationPaths.CreateSystem();
            paths.Validate();
            var service = new InstallationService(new SystemInstallIntegration());
            if (arguments.Silent)
            {
                var result = await service.InstallAsync(
                    payload,
                    paths,
                    new InstallationOptions(arguments.CreateDesktopShortcut),
                    progress: null,
                    CancellationToken.None);
                if (arguments.LaunchAfterInstall)
                {
                    LaunchApplication(result.ApplicationPath);
                }

                Shutdown(exitCode: 0);
                return;
            }

            var window = new InstallerWindow(service, payload, paths);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (Exception exception) when (exception is
                                           ArgumentException or
                                           InvalidDataException or
                                           IOException or
                                           UnauthorizedAccessException or
                                           InvalidOperationException or
                                           System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                $"RightMenuCheck 安装程序无法继续：{exception.Message}",
                "安装失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(exitCode: 1);
        }
    }

    internal static void LaunchApplication(string applicationPath)
    {
        var path = Path.GetFullPath(applicationPath);
        Process.Start(new ProcessStartInfo(path)
        {
            WorkingDirectory = Path.GetDirectoryName(path),
            UseShellExecute = true,
        });
    }
}
