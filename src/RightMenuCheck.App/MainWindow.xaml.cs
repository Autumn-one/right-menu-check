using System.Windows;
using RightMenuCheck.App.Services;
using RightMenuCheck.App.ViewModels;

namespace RightMenuCheck.App;

public partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel =>
        DataContext as MainWindowViewModel ??
        throw new InvalidOperationException("MainWindowViewModel is not configured.");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(new ContextMenuDataService());
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await ViewModel.ScanAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e) => ViewModel.Dispose();
}
