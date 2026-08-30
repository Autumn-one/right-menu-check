using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using RightMenuCheck.App.Services;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.App;

public partial class UpdateRequiredWindow : Window
{
    private readonly SignedUpdateManifest _manifest;
    private readonly IApplicationUpdateService _updateService;
    private bool _allowClose;
    private bool _isWorking;

    public UpdateRequiredWindow(
        IApplicationUpdateService updateService,
        SignedUpdateManifest manifest)
    {
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        InitializeComponent();
        VersionText.Text = $"版本 {_manifest.Payload.Version}";
        Closing += UpdateRequiredWindow_Closing;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking)
        {
            return;
        }

        _isWorking = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "正在更新";
        StatusText.Text = "正在下载并验证更新包…";
        DownloadProgress.Value = 0;
        var progress = new Progress<double>(value => DownloadProgress.Value = value * 100);
        try
        {
            await _updateService.PrepareAndLaunchAsync(
                _manifest,
                progress,
                CancellationToken.None);
            StatusText.Text = "更新程序已启动，正在关闭当前版本…";
            _allowClose = true;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is
                                           HttpRequestException or
                                           IOException or
                                           InvalidDataException or
                                           InvalidOperationException or
                                           TimeoutException or
                                           UnauthorizedAccessException)
        {
            StatusText.Text = $"暂时无法完成更新：{exception.Message}";
            UpdateButton.Content = "重试";
            UpdateButton.IsEnabled = true;
            _isWorking = false;
        }
    }

    private void UpdateRequiredWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Activate();
        }
    }
}
