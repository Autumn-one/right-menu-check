using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using RightMenuCheck.App.Services;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.App;

public partial class UpdateRequiredWindow : Window
{
    private readonly PreparedApplicationUpdate _preparedUpdate;
    private readonly IApplicationUpdateService _updateService;
    private bool _allowClose;
    private bool _isWorking;

    public UpdateRequiredWindow(
        IApplicationUpdateService updateService,
        PreparedApplicationUpdate preparedUpdate)
    {
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _preparedUpdate = preparedUpdate ?? throw new ArgumentNullException(nameof(preparedUpdate));
        InitializeComponent();
        VersionText.Text = $"版本 {_preparedUpdate.TargetVersion}";
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
        StatusText.Text = "正在启动更新程序…";
        UpdateProgress.IsIndeterminate = true;
        try
        {
            await _updateService.LaunchPreparedAsync(
                _preparedUpdate,
                CancellationToken.None);
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = 35;
            StatusText.Text = "更新程序已接管，正在关闭当前版本…";
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
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = 0;
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
