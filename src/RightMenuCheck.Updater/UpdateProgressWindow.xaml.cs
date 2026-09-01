using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace RightMenuCheck.Updater;

public partial class UpdateProgressWindow : Window
{
    private static readonly Brush FailureBrush = new SolidColorBrush(Color.FromRgb(181, 61, 55));
    private bool _allowClose;

    public UpdateProgressWindow()
    {
        InitializeComponent();
        Closing += UpdateProgressWindow_Closing;
    }

    internal void Report(UpdateProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Apply(snapshot));
            return;
        }

        Apply(snapshot);
    }

    internal void ShowCompleted() => Report(
        new UpdateProgressSnapshot(100, "更新完成，新版本已经启动。"));

    internal void ShowFailure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowFailure(message));
            return;
        }

        StatusText.Text = "更新未完成";
        DetailText.Text = message;
        InstallProgress.Foreground = FailureBrush;
        PercentText.Foreground = FailureBrush;
    }

    internal void AllowClose() => _allowClose = true;

    private void Apply(UpdateProgressSnapshot snapshot)
    {
        var percentage = Math.Clamp(snapshot.Percentage, InstallProgress.Value, 100);
        InstallProgress.Value = percentage;
        PercentText.Text = $"{percentage:0}%";
        StatusText.Text = snapshot.Status;
    }

    private void UpdateProgressWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Activate();
        }
    }
}
