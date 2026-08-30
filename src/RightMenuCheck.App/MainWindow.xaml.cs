using System.Windows;
using RightMenuCheck.App.ViewModels;

namespace RightMenuCheck.App;

public partial class MainWindow : Window
{
    public event EventHandler? InitialScanCompleted;

    private MainWindowViewModel ViewModel =>
        DataContext as MainWindowViewModel ??
        throw new InvalidOperationException("MainWindowViewModel is not configured.");

    public MainWindow(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await ViewModel.ScanAsync();
        InitialScanCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void MainWindow_Closed(object? sender, EventArgs e) => ViewModel.Dispose();
}
