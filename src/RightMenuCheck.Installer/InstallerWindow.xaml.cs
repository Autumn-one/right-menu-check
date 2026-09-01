using System.ComponentModel;
using System.IO;
using System.Windows;
using RightMenuCheck.Installation;

namespace RightMenuCheck.Installer;

public partial class InstallerWindow : Window
{
    private readonly InstallationPaths _paths;
    private readonly IInstallationPayloadSource _payload;
    private readonly InstallationService _service;
    private bool _completed;
    private bool _working;

    public InstallerWindow(
        InstallationService service,
        IInstallationPayloadSource payload,
        InstallationPaths paths)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        InitializeComponent();
        VersionText.Text = $"版本 {_payload.ExpectedVersion} · 当前用户安装";
        InstallPathText.Text = _paths.InstallDirectory;
        Closing += InstallerWindow_Closing;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_completed)
        {
            Close();
            return;
        }

        if (_working)
        {
            return;
        }

        _working = true;
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        DesktopShortcutCheck.IsEnabled = false;
        LaunchAfterInstallCheck.IsEnabled = false;
        var progress = new Progress<InstallationProgress>(value =>
        {
            InstallProgress.Value = Math.Max(InstallProgress.Value, value.Percentage);
            StatusText.Text = value.Status;
        });
        try
        {
            var result = await _service.InstallAsync(
                _payload,
                _paths,
                new InstallationOptions(DesktopShortcutCheck.IsChecked == true),
                progress,
                CancellationToken.None);
            InstallProgress.Value = 100;
            StatusText.Text = "安装完成。";
            _completed = true;
            _working = false;
            InstallButton.Content = "完成";
            InstallButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            if (LaunchAfterInstallCheck.IsChecked == true)
            {
                App.LaunchApplication(result.ApplicationPath);
            }
        }
        catch (Exception exception) when (exception is
                                           ArgumentException or
                                           InvalidDataException or
                                           IOException or
                                           UnauthorizedAccessException or
                                           InvalidOperationException or
                                           System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"安装未完成：{exception.Message}";
            InstallProgress.Value = 0;
            InstallButton.Content = "重试";
            InstallButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            DesktopShortcutCheck.IsEnabled = true;
            LaunchAfterInstallCheck.IsEnabled = true;
            _working = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void InstallerWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_working)
        {
            e.Cancel = true;
            Activate();
        }
    }
}
