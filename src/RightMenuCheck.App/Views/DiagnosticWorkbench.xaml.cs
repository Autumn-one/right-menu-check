using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RightMenuCheck.App.ViewModels;
using RightMenuCheck.Core.Inventory;

namespace RightMenuCheck.App.Views;

public partial class DiagnosticWorkbench : UserControl
{
    private static readonly JsonSerializerOptions ExportSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public DiagnosticWorkbench()
    {
        InitializeComponent();
    }

    private MainWindowViewModel ViewModel =>
        DataContext as MainWindowViewModel ??
        throw new InvalidOperationException("The diagnostic workbench requires MainWindowViewModel.");

    private async void Scan_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.ScanAsync();

    private async void BenchmarkSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = MenuGrid.SelectedItems
            .Cast<ContextMenuRowViewModel>()
            .ToArray();
        await ViewModel.BenchmarkAsync(selected);
    }

    private async void BenchmarkAggregate_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.BenchmarkAggregateAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private void BrowseSample_Click(object sender, RoutedEventArgs e)
    {
        var targetKind = ViewModel.SelectedItem?.Registration.TargetKind;
        var needsFile = targetKind is ContextMenuTargetKind.File or
            ContextMenuTargetKind.FileType or
            ContextMenuTargetKind.AllFileSystemObjects;
        var owner = Window.GetWindow(this);
        if (needsFile)
        {
            var dialog = new OpenFileDialog
            {
                CheckFileExists = true,
                Multiselect = false,
                Title = "选择右键菜单测试文件",
            };
            if (dialog.ShowDialog(owner) == true)
            {
                ViewModel.SamplePath = dialog.FileName;
            }

            return;
        }

        var folderDialog = new OpenFolderDialog
        {
            Multiselect = false,
            Title = "选择右键菜单测试文件夹",
        };
        if (folderDialog.ShowDialog(owner) == true)
        {
            ViewModel.SamplePath = folderDialog.FolderName;
        }
    }

    private void ClearSample_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SamplePath = string.Empty;

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".json",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"RightMenuCheck-{timestamp}.json",
            Title = "导出诊断结果",
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        try
        {
            await using var stream = new FileStream(
                dialog.FileName,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous);
            await JsonSerializer.SerializeAsync(
                stream,
                ViewModel.CreateExportRows(),
                ExportSerializerOptions,
                CancellationToken.None);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowExportError(owner, exception.Message);
        }
        catch (IOException exception)
        {
            ShowExportError(owner, exception.Message);
        }
    }

    private static void ShowExportError(Window? owner, string message)
    {
        _ = owner is null
            ? MessageBox.Show(
                message,
                "导出失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error)
            : MessageBox.Show(
                owner,
                message,
                "导出失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
    }
}
