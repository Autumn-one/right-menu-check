using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using RightMenuCheck.ReleaseManager.Services;
using RightMenuCheck.ReleaseManager.ViewModels;
using RightMenuCheck.ReleaseManager.Views;

namespace RightMenuCheck.ReleaseManager;

public partial class MainWindow : Window
{
    private readonly ReleaseManagerViewModel _viewModel;

    public MainWindow(ReleaseManagerViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(_viewModel.InitializeAsync).ConfigureAwait(true);

    private void OnClosing(object? sender, CancelEventArgs e) => _viewModel.Cancel();

    private async void RefreshReleasesClick(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(_viewModel.RefreshReleasesAsync).ConfigureAwait(true);

    private async void RefreshAnnouncementsClick(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(_viewModel.RefreshAnnouncementsAsync).ConfigureAwait(true);

    private async void PublishClick(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(_viewModel.PublishAsync).ConfigureAwait(true);

    private async void SaveAnnouncementClick(object sender, RoutedEventArgs e) =>
        await ExecuteAsync(_viewModel.SaveAnnouncementAsync).ConfigureAwait(true);

    private void NewAnnouncementClick(object sender, RoutedEventArgs e) =>
        _viewModel.NewAnnouncement();

    private async void DeleteReleaseClick(object sender, RoutedEventArgs e)
    {
        var impact = _viewModel.PreviewSelectedDeletion();
        if (impact is null || !Confirm(
                "确认删除发布版本",
                impact.CreatePreview(),
                "删除版本"))
        {
            return;
        }

        await ExecuteAsync(() => _viewModel.DeleteReleaseAsync(impact)).ConfigureAwait(true);
    }

    private async void WithdrawAnnouncementClick(object sender, RoutedEventArgs e)
    {
        var preview = _viewModel.CreateWithdrawalPreview();
        if (preview is null || !Confirm("确认撤下公告", preview, "撤下公告"))
        {
            return;
        }

        await ExecuteAsync(_viewModel.WithdrawSelectedAnnouncementAsync).ConfigureAwait(true);
    }

    private void OpenReleaseClick(object sender, RoutedEventArgs e)
    {
        var url = _viewModel.SelectedRelease?.HtmlUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void CancelClick(object sender, RoutedEventArgs e) => _viewModel.Cancel();

    private bool Confirm(string title, string message, string confirmationLabel)
    {
        var dialog = new ConfirmationDialog(title, message, confirmationLabel)
        {
            Owner = this,
        };
        return dialog.ShowDialog() == true;
    }

    private async Task ExecuteAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is
               GitHub.GitHubApiException or
               IOException or
               HttpRequestException or
               Win32Exception or
               CryptographicException or
               UnauthorizedAccessException or
               InvalidDataException or
               InvalidOperationException or
               ArgumentException or
               FormatException or
               Publishing.PublishScriptException or
               Publishing.RemoteReleaseIncompleteException)
        {
            _viewModel.ReportError(exception);
        }
    }
}
