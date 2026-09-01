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
using RightMenuCheck.Windows.Management;

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
        if (selected.Length > 20 && !Confirm(
                $"将依次测试 {selected.Length} 项，每项会启动多个隔离进程。是否继续？",
                "确认批量测试"))
        {
            return;
        }

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

    private async void WindowsContextMenuMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox)
        {
            return;
        }

        var useWindows11Mode = checkBox.IsChecked == true;
        var plan = ViewModel.PreviewWindowsContextMenuMode(useWindows11Mode);
        if (!plan.IsSupported)
        {
            ViewModel.RefreshWindowsContextMenuStatus();
            ShowInformation(plan.BlockReason ?? "无法更改系统右键菜单模式。", "无法更改右键样式");
            return;
        }

        if (plan.IsNoChange)
        {
            ViewModel.RefreshWindowsContextMenuStatus();
            return;
        }

        var targetName = useWindows11Mode ? "Windows 11 简洁菜单" : "经典完整菜单";
        var message = $"切换为{targetName}？\n\n" +
                      $"{plan.ImpactDescription}\n\n" +
                      "这是 Windows 的兼容覆盖，并非 Microsoft 公开设置；未来系统更新可能使其失效。";
        if (!Confirm(message, "确认更改右键样式"))
        {
            ViewModel.RefreshWindowsContextMenuStatus();
            return;
        }

        _ = await ViewModel.ApplyWindowsContextMenuModeAsync(useWindows11Mode);
    }

    private async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm(
                "重启 Explorer 以应用右键菜单样式？\n\n所有已打开的文件资源管理器窗口都会关闭。",
                "确认重启 Explorer"))
        {
            return;
        }

        var result = await ViewModel.RestartExplorerAsync();
        if (result is { Succeeded: false })
        {
            ShowInformation(result.Message, "Explorer 重启未完成");
        }
    }

    private async void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        var backupPath = SelectBackupPath("menu-backup");
        if (backupPath is null)
        {
            return;
        }

        await ViewModel.BackupAsync(backupPath, GetSelectedRows());
    }

    private async void ToggleState_Click(object sender, RoutedEventArgs e)
    {
        var plan = ViewModel.PreviewSelectedState();
        if (plan is null || !plan.IsSupported)
        {
            ShowInformation(plan?.BlockReason ?? "请选择一个菜单项。", "无法更改状态");
            return;
        }

        if (plan.IsNoChange)
        {
            ShowInformation("当前状态无需更改。", "菜单状态");
            return;
        }

        var actionName = plan.Action == ContextMenuStateAction.Disable ? "禁用" : "启用";
        var confirmation = $"{actionName}“{ViewModel.SelectedItem?.DisplayName}”？\n\n" +
                           $"{plan.ImpactDescription}\n\n操作前将强制创建备份。";
        if (!Confirm(confirmation, $"确认{actionName}"))
        {
            return;
        }

        var backupPath = SelectBackupPath($"before-{actionName}");
        if (backupPath is not null)
        {
            _ = await ViewModel.ExecuteSelectedStateAsync(backupPath);
        }
    }

    private async void RemoveRegistration_Click(object sender, RoutedEventArgs e)
    {
        var plan = ViewModel.PreviewSelectedRemoval();
        if (plan is null || !plan.IsSupported)
        {
            ShowInformation(plan?.BlockReason ?? "请选择一个菜单项。", "无法删除注册");
            return;
        }

        if (plan.IsNoChange)
        {
            ShowInformation("该注册键已经不存在。", "删除注册");
            return;
        }

        var message = $"删除“{ViewModel.SelectedItem?.DisplayName}”的菜单注册？\n\n" +
                      $"{plan.ImpactDescription}\n\n删除前将强制创建可恢复备份。";
        if (!Confirm(message, "确认删除注册"))
        {
            return;
        }

        var backupPath = SelectBackupPath("before-remove");
        if (backupPath is not null)
        {
            _ = await ViewModel.ExecuteSelectedRemovalAsync(backupPath);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "RightMenuCheck 备份 (*.rmcbak)|*.rmcbak",
            Multiselect = false,
            Title = "选择要恢复的备份",
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        try
        {
            var plan = await ViewModel.CreateRestorePlanAsync(dialog.FileName);
            if (!plan.CanExecute)
            {
                ShowInformation(plan.BlockReason ?? "备份无法恢复。", "恢复被阻止");
                return;
            }

            var message = "以 Exact 模式恢复此备份？\n\n" +
                          $"创建键：{plan.Preflight.KeysToCreate}\n" +
                          $"更新键：{plan.Preflight.KeysToUpdate}\n" +
                          $"写入值：{plan.Preflight.ValuesToWrite}\n" +
                          $"冲突：{plan.Preflight.Conflicts.Count}\n\n" +
                          "选定注册根将按备份精确重建。";
            if (Confirm(message, "确认恢复"))
            {
                _ = await ViewModel.ExecuteRestoreAsync(
                    plan,
                    acceptConflicts: plan.Preflight.Conflicts.Count > 0);
            }
        }
        catch (InvalidDataException exception)
        {
            ShowInformation(exception.Message, "备份无效");
        }
        catch (IOException exception)
        {
            ShowInformation(exception.Message, "读取备份失败");
        }
    }

    private async void UninstallApplication_Click(object sender, RoutedEventArgs e)
    {
        var plan = ViewModel.PreviewSelectedUninstall();
        if (plan is null || !plan.IsSupported)
        {
            ShowInformation(plan?.BlockReason ?? "没有可确认的所属应用。", "无法卸载应用");
            return;
        }

        var method = plan.Method switch
        {
            ApplicationUninstallMethod.PackageCurrentUser => "移除当前用户的 MSIX/AppX 包",
            ApplicationUninstallMethod.MsiProductCode => "启动 Windows Installer 卸载精确 ProductCode",
            ApplicationUninstallMethod.VendorExecutable => "直接启动该应用登记的卸载程序",
            _ => "启动应用卸载流程",
        };
        var elevation = plan.RequiresElevation
            ? "此卸载步骤会请求管理员权限。\n"
            : string.Empty;
        var message = $"卸载整个“{plan.Owner.DisplayName}”？\n\n" +
                      $"将执行：{method}。\n" +
                      elevation +
                      "确认后会先让你选择 .rmcbak 文件，并强制备份该应用当前匹配到的右键菜单注册；" +
                      "只有备份成功才会启动卸载。\n\n" +
                      "这不是删除当前右键菜单项，而是卸载整个应用。备份只能恢复注册信息，不能重新安装应用。";
        if (!Confirm(message, "确认卸载应用"))
        {
            return;
        }

        var backupPath = SelectBackupPath("before-uninstall");
        if (backupPath is not null)
        {
            _ = await ViewModel.ExecuteSelectedUninstallAsync(plan, backupPath);
        }
    }

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

    private ContextMenuRowViewModel[] GetSelectedRows() => MenuGrid.SelectedItems
        .Cast<ContextMenuRowViewModel>()
        .ToArray();

    private string? SelectBackupPath(string prefix)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".rmcbak",
            Filter = "RightMenuCheck 备份 (*.rmcbak)|*.rmcbak",
            FileName = $"{prefix}-{timestamp}.rmcbak",
            OverwritePrompt = true,
            Title = "选择备份保存位置",
        };
        return dialog.ShowDialog(Window.GetWindow(this)) == true ? dialog.FileName : null;
    }

    private bool Confirm(string message, string title) => MessageBox.Show(
        Window.GetWindow(this),
        message,
        title,
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    private void ShowInformation(string message, string title) => MessageBox.Show(
        Window.GetWindow(this),
        message,
        title,
        MessageBoxButton.OK,
        MessageBoxImage.Information);
}
