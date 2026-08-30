using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows.Data;
using RightMenuCheck.App.Services;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Probe.Protocol;
using RightMenuCheck.Windows.Benchmark;

namespace RightMenuCheck.App.ViewModels;

public enum MenuCategoryFilter
{
    All,
    File,
    Folder,
    Background,
    DriveAndDesktop,
    Modern,
}

public sealed record MenuCategoryOption(MenuCategoryFilter Value, string Label);

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IContextMenuDataService _dataService;
    private CancellationTokenSource? _operationCancellation;
    private MenuCategoryFilter _selectedCategory;
    private string _searchText = string.Empty;
    private bool _onlyThirdParty;
    private bool _onlyEnabled;
    private bool _includeStaticCommands;
    private bool _isBusy;
    private string _statusText = "准备扫描";
    private int _progressValue;
    private int _progressMaximum = 1;
    private string _samplePath = string.Empty;
    private int _trialCount = BenchmarkOptions.Default.TrialCount;
    private int _visibleCount;
    private int _issueCount;
    private ContextMenuRowViewModel? _selectedItem;
    private string _aggregateResult = "未测试";

    public MainWindowViewModel(IContextMenuDataService dataService)
    {
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        ApplyDefaultSort();
    }

    public ObservableCollection<ContextMenuRowViewModel> Items { get; } = [];

    public ICollectionView ItemsView { get; }

    public IReadOnlyList<MenuCategoryOption> Categories { get; } =
    [
        new(MenuCategoryFilter.All, "全部"),
        new(MenuCategoryFilter.File, "文件"),
        new(MenuCategoryFilter.Folder, "文件夹"),
        new(MenuCategoryFilter.Background, "空白处"),
        new(MenuCategoryFilter.DriveAndDesktop, "磁盘 / 桌面"),
        new(MenuCategoryFilter.Modern, "现代菜单"),
    ];

    public IReadOnlyList<int> TrialCounts { get; } = [3, 5, 7, 10, 15, 20];

    public MenuCategoryFilter SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                RefreshFilter();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool OnlyThirdParty
    {
        get => _onlyThirdParty;
        set
        {
            if (SetProperty(ref _onlyThirdParty, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool OnlyEnabled
    {
        get => _onlyEnabled;
        set
        {
            if (SetProperty(ref _onlyEnabled, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool IncludeStaticCommands
    {
        get => _includeStaticCommands;
        set
        {
            if (SetProperty(ref _includeStaticCommands, value))
            {
                RefreshFilter();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public bool CanStart => !IsBusy;

    public bool CanCancel => IsBusy;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public int ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public int ProgressMaximum
    {
        get => _progressMaximum;
        private set => SetProperty(ref _progressMaximum, Math.Max(1, value));
    }

    public string SamplePath
    {
        get => _samplePath;
        set => SetProperty(ref _samplePath, value);
    }

    public int TrialCount
    {
        get => _trialCount;
        set => SetProperty(ref _trialCount, value);
    }

    public int VisibleCount
    {
        get => _visibleCount;
        private set => SetProperty(ref _visibleCount, value);
    }

    public int IssueCount
    {
        get => _issueCount;
        private set => SetProperty(ref _issueCount, value);
    }

    public ContextMenuRowViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public string AggregateResult
    {
        get => _aggregateResult;
        private set => SetProperty(ref _aggregateResult, value);
    }

    public async Task ScanAsync()
    {
        if (IsBusy)
        {
            return;
        }

        BeginOperation("正在扫描注册表与应用包…");
        var progress = new Progress<ScanProgress>(UpdateScanProgress);
        try
        {
            var snapshot = await _dataService.ScanAsync(
                progress,
                _operationCancellation!.Token);
            Items.Clear();
            foreach (var metadata in snapshot.Items)
            {
                Items.Add(new ContextMenuRowViewModel(metadata));
            }

            SelectedItem = Items.FirstOrDefault();

            IssueCount = snapshot.RegistryIssues.Count +
                         snapshot.PackageIssues.Count +
                         snapshot.MetadataIssues.Count;
            ApplyDefaultSort();
            RefreshFilter();
            StatusText = $"已发现 {Items.Count} 项 · {snapshot.Duration.TotalSeconds:F1} 秒";
        }
        catch (OperationCanceledException)
        {
            StatusText = "操作已取消";
        }
        catch (UnauthorizedAccessException exception)
        {
            SetOperationFailure(exception);
        }
        catch (SecurityException exception)
        {
            SetOperationFailure(exception);
        }
        catch (IOException exception)
        {
            SetOperationFailure(exception);
        }
        catch (InvalidOperationException exception)
        {
            SetOperationFailure(exception);
        }
        catch (Win32Exception exception)
        {
            SetOperationFailure(exception);
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task BenchmarkAsync(IReadOnlyList<ContextMenuRowViewModel> selectedItems)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        if (IsBusy)
        {
            return;
        }

        var rows = selectedItems.Distinct().ToArray();
        if (rows.Length == 0)
        {
            StatusText = "请选择至少一个菜单项";
            return;
        }

        BeginOperation($"准备测试 {rows.Length} 项…");
        ProgressMaximum = rows.Length;
        try
        {
            for (var index = 0; index < rows.Length; index++)
            {
                _operationCancellation!.Token.ThrowIfCancellationRequested();
                var row = rows[index];
                ProgressValue = index;
                StatusText = $"正在测试 {index + 1}/{rows.Length} · {row.DisplayName}";
                if (!TryCreateTarget(row, out var target, out var error))
                {
                    row.SetOperationError(error);
                    continue;
                }

                try
                {
                    var result = await _dataService.BenchmarkAsync(
                        row.Metadata,
                        target,
                        new BenchmarkOptions(
                            TrialCount,
                            BenchmarkOptions.Default.Timeout),
                        _operationCancellation.Token);
                    row.SetBenchmark(result);
                }
                catch (FileNotFoundException exception)
                {
                    row.SetOperationError(exception.Message);
                }
                catch (ArgumentException exception)
                {
                    row.SetOperationError(exception.Message);
                }
                catch (InvalidOperationException exception)
                {
                    row.SetOperationError(exception.Message);
                }

                ItemsView.Refresh();
            }

            ProgressValue = rows.Length;
            StatusText = $"已完成 {rows.Length} 项测试";
            ApplyDefaultSort();
        }
        catch (OperationCanceledException)
        {
            StatusText = "测试已取消";
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task BenchmarkAggregateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var row = SelectedItem ?? Items.FirstOrDefault();
        if (row is null)
        {
            StatusText = "没有可测试的菜单项";
            return;
        }

        if (!TryCreateTarget(row, out var target, out var error, requireTypedSample: false))
        {
            StatusText = error;
            return;
        }

        BeginOperation("正在测试整体菜单…");
        ProgressMaximum = TrialCount;
        try
        {
            var result = await _dataService.BenchmarkAggregateAsync(
                target,
                new BenchmarkOptions(TrialCount, TimeSpan.FromSeconds(15)),
                _operationCancellation!.Token);
            AggregateResult = result.HandlerDuration is null
                ? result.Status.ToString()
                : $"中位数 {result.HandlerDuration.Median:F2} ms · P95 {result.HandlerDuration.Percentile95:F2} ms";
            ProgressValue = TrialCount;
            StatusText = "整体菜单测试完成";
        }
        catch (OperationCanceledException)
        {
            StatusText = "整体菜单测试已取消";
        }
        catch (FileNotFoundException exception)
        {
            StatusText = exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    public void Cancel() => _operationCancellation?.Cancel();

    public void Dispose()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    public IReadOnlyList<ContextMenuExportRow> CreateExportRows() =>
        Items.Select(static item => item.ToExport()).ToArray();

    private bool FilterItem(object value)
    {
        if (value is not ContextMenuRowViewModel row)
        {
            return false;
        }

        if (OnlyThirdParty && !row.IsThirdParty)
        {
            return false;
        }

        if (OnlyEnabled && !row.IsEnabled)
        {
            return false;
        }

        if (!IncludeStaticCommands && row.IsStaticOnly)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !row.SearchText.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        return SelectedCategory switch
        {
            MenuCategoryFilter.All => true,
            MenuCategoryFilter.File => row.Registration.TargetKind is
                ContextMenuTargetKind.File or
                ContextMenuTargetKind.FileType or
                ContextMenuTargetKind.AllFileSystemObjects,
            MenuCategoryFilter.Folder => row.Registration.TargetKind is
                ContextMenuTargetKind.Folder or
                ContextMenuTargetKind.LibraryFolder,
            MenuCategoryFilter.Background => row.Registration.TargetKind is
                ContextMenuTargetKind.FolderBackground or
                ContextMenuTargetKind.LibraryBackground,
            MenuCategoryFilter.DriveAndDesktop => row.Registration.TargetKind is
                ContextMenuTargetKind.Drive or
                ContextMenuTargetKind.DesktopBackground,
            MenuCategoryFilter.Modern => row.IsModern,
            _ => true,
        };
    }

    private bool TryCreateTarget(
        ContextMenuRowViewModel row,
        out BenchmarkTarget target,
        out string error,
        bool requireTypedSample = true)
    {
        var probeKind = MapProbeTarget(row.Registration.TargetKind);
        var samplePath = SamplePath.Trim();
        if (requireTypedSample &&
            row.Registration.TargetKind == ContextMenuTargetKind.FileType &&
            samplePath.Length == 0)
        {
            target = null!;
            error = $"{row.Scope} 需要选择匹配的文件样本";
            return false;
        }

        var path = samplePath.Length > 0 ? samplePath : GetDefaultTargetPath(probeKind);
        if (probeKind == ProbeTargetKind.File && !File.Exists(path))
        {
            target = null!;
            error = "文件样本不存在";
            return false;
        }

        if (probeKind != ProbeTargetKind.File && !Directory.Exists(path))
        {
            target = null!;
            error = "文件夹样本不存在";
            return false;
        }

        if (requireTypedSample && row.Registration.ClassPath.StartsWith('.') &&
            !Path.GetExtension(path).Equals(
                row.Registration.ClassPath,
                StringComparison.OrdinalIgnoreCase))
        {
            target = null!;
            error = $"请选择 {row.Registration.ClassPath} 文件样本";
            return false;
        }

        target = new BenchmarkTarget(probeKind, path);
        error = string.Empty;
        return true;
    }

    private static ProbeTargetKind MapProbeTarget(ContextMenuTargetKind targetKind) => targetKind switch
    {
        ContextMenuTargetKind.File or
            ContextMenuTargetKind.FileType or
            ContextMenuTargetKind.AllFileSystemObjects => ProbeTargetKind.File,
        ContextMenuTargetKind.Folder or
            ContextMenuTargetKind.LibraryFolder => ProbeTargetKind.Folder,
        ContextMenuTargetKind.FolderBackground or
            ContextMenuTargetKind.LibraryBackground => ProbeTargetKind.FolderBackground,
        ContextMenuTargetKind.Drive => ProbeTargetKind.Drive,
        ContextMenuTargetKind.DesktopBackground => ProbeTargetKind.DesktopBackground,
        _ => ProbeTargetKind.File,
    };

    private static string GetDefaultTargetPath(ProbeTargetKind targetKind) => targetKind switch
    {
        ProbeTargetKind.File => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "win.ini"),
        ProbeTargetKind.Drive => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
        ProbeTargetKind.DesktopBackground =>
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        _ => Path.GetTempPath(),
    };

    private void BeginOperation(string status)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        ProgressValue = 0;
        ProgressMaximum = 1;
        StatusText = status;
    }

    private void EndOperation()
    {
        IsBusy = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private void UpdateScanProgress(ScanProgress progress)
    {
        ProgressMaximum = Math.Max(1, progress.Total);
        ProgressValue = progress.Completed;
        StatusText = progress.Stage switch
        {
            ScanStage.Discovering => "正在扫描注册表与应用包…",
            ScanStage.Enriching => $"正在解析处理器 {progress.Completed}/{progress.Total}",
            ScanStage.Completed => "扫描完成",
            _ => StatusText,
        };
    }

    private void RefreshFilter()
    {
        ItemsView.Refresh();
        VisibleCount = ItemsView.Cast<object>().Count();
    }

    private void ApplyDefaultSort()
    {
        ItemsView.SortDescriptions.Clear();
        ItemsView.SortDescriptions.Add(new SortDescription(
            nameof(ContextMenuRowViewModel.BenchmarkSortRank),
            ListSortDirection.Ascending));
        ItemsView.SortDescriptions.Add(new SortDescription(
            nameof(ContextMenuRowViewModel.Percentile95Value),
            ListSortDirection.Descending));
        ItemsView.SortDescriptions.Add(new SortDescription(
            nameof(ContextMenuRowViewModel.DisplayName),
            ListSortDirection.Ascending));
    }

    private void SetOperationFailure(Exception exception)
    {
        StatusText = $"操作失败：{exception.Message}";
        IssueCount++;
    }
}
