using System.Collections.ObjectModel;
using System.Globalization;
using RightMenuCheck.Distribution;
using RightMenuCheck.ReleaseManager.Announcements;
using RightMenuCheck.ReleaseManager.Configuration;
using RightMenuCheck.ReleaseManager.GitHub;
using RightMenuCheck.ReleaseManager.Infrastructure;
using RightMenuCheck.ReleaseManager.Publishing;
using RightMenuCheck.ReleaseManager.Services;

namespace RightMenuCheck.ReleaseManager.ViewModels;

public sealed record AnnouncementKindOption(AnnouncementKind Value, string Label);

public sealed class ReleaseManagerViewModel : ObservableObject, IDisposable
{
    private readonly IGitHubRepositoryClient _github;
    private readonly ReleaseAdministrationService _releaseAdministration;
    private readonly ReleasePublishingService _publishing;
    private readonly AnnouncementManagementService _announcements;
    private CancellationTokenSource? _operationCancellation;
    private GitHubRelease? _selectedRelease;
    private AnnouncementMessage? _selectedAnnouncement;
    private bool _deleteTag;
    private bool _isBusy;
    private string _statusText = "准备就绪";
    private string _publishVersion = "0.1.0";
    private string _releaseNotes = string.Empty;
    private bool _isPrerelease;
    private string _announcementId = string.Empty;
    private string _announcementTitle = string.Empty;
    private string _announcementBody = string.Empty;
    private AnnouncementKind _announcementKind;
    private string _announcementStartsAtUtc = FormatUtc(DateTimeOffset.UtcNow);
    private string _announcementEndsAtUtc = string.Empty;
    private string _minimumVersion = string.Empty;
    private string _maximumVersion = string.Empty;

    public ReleaseManagerViewModel(
        ReleaseManagerConfiguration configuration,
        IGitHubRepositoryClient github,
        ReleaseAdministrationService releaseAdministration,
        ReleasePublishingService publishing,
        AnnouncementManagementService announcements)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _releaseAdministration = releaseAdministration ??
                                 throw new ArgumentNullException(nameof(releaseAdministration));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _announcements = announcements ?? throw new ArgumentNullException(nameof(announcements));
    }

    public ReleaseManagerConfiguration Configuration { get; }

    public string RepositoryLabel => Configuration.Repository.ToString();

    public string BranchLabel => Configuration.DefaultBranch;

    public bool PublishScriptAcceptsVersion => _publishing.SupportsVersionArgument;

    public bool ShowVersionCompatibilityNotice => !PublishScriptAcceptsVersion;

    public ObservableCollection<GitHubRelease> Releases { get; } = [];

    public ObservableCollection<AnnouncementMessage> AnnouncementMessages { get; } = [];

    public IReadOnlyList<AnnouncementKindOption> AnnouncementKinds { get; } =
    [
        new(AnnouncementKind.Information, "信息"),
        new(AnnouncementKind.Warning, "警告"),
        new(AnnouncementKind.Maintenance, "维护"),
    ];

    public GitHubRelease? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (SetProperty(ref _selectedRelease, value))
            {
                OnPropertyChanged(nameof(HasSelectedRelease));
                OnPropertyChanged(nameof(CanUseSelectedRelease));
            }
        }
    }

    public bool HasSelectedRelease => SelectedRelease is not null;

    public bool CanUseSelectedRelease => IsReady && HasSelectedRelease;

    public AnnouncementMessage? SelectedAnnouncement
    {
        get => _selectedAnnouncement;
        set
        {
            if (SetProperty(ref _selectedAnnouncement, value))
            {
                LoadAnnouncementEditor(value);
                OnPropertyChanged(nameof(IsEditingAnnouncement));
                OnPropertyChanged(nameof(AnnouncementSaveLabel));
                OnPropertyChanged(nameof(CanWithdrawAnnouncement));
            }
        }
    }

    public bool IsEditingAnnouncement => SelectedAnnouncement is not null;

    public string AnnouncementSaveLabel => IsEditingAnnouncement ? "发布修订" : "新增公告";

    public bool CanWithdrawAnnouncement => IsReady && IsEditingAnnouncement;

    public bool DeleteTag
    {
        get => _deleteTag;
        set => SetProperty(ref _deleteTag, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(CanUseSelectedRelease));
                OnPropertyChanged(nameof(CanWithdrawAnnouncement));
            }
        }
    }

    public bool IsReady => !IsBusy;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string PublishVersion
    {
        get => _publishVersion;
        set => SetProperty(ref _publishVersion, value);
    }

    public string ReleaseNotes
    {
        get => _releaseNotes;
        set => SetProperty(ref _releaseNotes, value);
    }

    public bool IsPrerelease
    {
        get => _isPrerelease;
        set => SetProperty(ref _isPrerelease, value);
    }

    public string AnnouncementId
    {
        get => _announcementId;
        set => SetProperty(ref _announcementId, value);
    }

    public string AnnouncementTitle
    {
        get => _announcementTitle;
        set => SetProperty(ref _announcementTitle, value);
    }

    public string AnnouncementBody
    {
        get => _announcementBody;
        set => SetProperty(ref _announcementBody, value);
    }

    public AnnouncementKind AnnouncementKind
    {
        get => _announcementKind;
        set => SetProperty(ref _announcementKind, value);
    }

    public string AnnouncementStartsAtUtc
    {
        get => _announcementStartsAtUtc;
        set => SetProperty(ref _announcementStartsAtUtc, value);
    }

    public string AnnouncementEndsAtUtc
    {
        get => _announcementEndsAtUtc;
        set => SetProperty(ref _announcementEndsAtUtc, value);
    }

    public string MinimumVersion
    {
        get => _minimumVersion;
        set => SetProperty(ref _minimumVersion, value);
    }

    public string MaximumVersion
    {
        get => _maximumVersion;
        set => SetProperty(ref _maximumVersion, value);
    }

    public async Task InitializeAsync()
    {
        await RefreshReleasesAsync().ConfigureAwait(true);
        await RefreshAnnouncementsAsync().ConfigureAwait(true);
    }

    public Task RefreshReleasesAsync() => RunAsync(
        "正在读取发布历史…",
        async cancellationToken =>
        {
            var releases = await _github.ListReleasesAsync(cancellationToken).ConfigureAwait(true);
            Replace(
                Releases,
                releases.OrderByDescending(static release =>
                    release.PublishedAtUtc ?? release.CreatedAtUtc));
            SelectedRelease = Releases.FirstOrDefault();
            StatusText = $"已读取 {Releases.Count} 个发布版本";
        });

    public ReleaseDeletionImpact? PreviewSelectedDeletion() => SelectedRelease is null
        ? null
        : ReleaseAdministrationService.PreviewDeletion(SelectedRelease, DeleteTag);

    public Task DeleteReleaseAsync(ReleaseDeletionImpact confirmedImpact) => RunAsync(
        "正在删除指定发布版本…",
        async cancellationToken =>
        {
            var result = await _releaseAdministration.DeleteAsync(
                confirmedImpact,
                cancellationToken).ConfigureAwait(true);
            StatusText = result.TagDeleted
                ? $"已删除 Release ID {result.ReleaseId} 及 tag {result.ExactTag}"
                : $"已删除 Release ID {result.ReleaseId}，tag 已保留";
            await ReloadReleasesWithinOperationAsync(cancellationToken).ConfigureAwait(true);
        });

    public Task PublishAsync() => RunAsync(
        "正在准备发布…",
        async cancellationToken =>
        {
            var progress = new Progress<ReleasePublishingProgress>(value =>
                StatusText = value.Message);
            var result = await _publishing.PublishAsync(
                new ReleasePublishingRequest(PublishVersion, ReleaseNotes, IsPrerelease),
                progress,
                cancellationToken).ConfigureAwait(true);
            StatusText = $"{result.Release.TagName} 发布完成 · {result.Artifact.Sha256[..12]}";
            await ReloadReleasesWithinOperationAsync(cancellationToken).ConfigureAwait(true);
        });

    public Task RefreshAnnouncementsAsync() => RunAsync(
        "正在读取公告…",
        async cancellationToken =>
        {
            var state = await _announcements.LoadAsync(cancellationToken).ConfigureAwait(true);
            Replace(AnnouncementMessages, state.Feed.Payload.Messages);
            SelectedAnnouncement = AnnouncementMessages.FirstOrDefault();
            if (SelectedAnnouncement is null)
            {
                NewAnnouncement();
            }

            StatusText = $"已读取 {AnnouncementMessages.Count} 条公告";
        });

    public void NewAnnouncement()
    {
        _selectedAnnouncement = null;
        OnPropertyChanged(nameof(SelectedAnnouncement));
        OnPropertyChanged(nameof(IsEditingAnnouncement));
        OnPropertyChanged(nameof(AnnouncementSaveLabel));
        OnPropertyChanged(nameof(CanWithdrawAnnouncement));
        LoadAnnouncementEditor(null);
    }

    public Task SaveAnnouncementAsync() => RunAsync(
        IsEditingAnnouncement ? "正在发布公告修订…" : "正在新增公告…",
        async cancellationToken =>
        {
            var input = CreateAnnouncementInput();
            var state = SelectedAnnouncement is { } selected
                ? await _announcements.ReviseAsync(
                        selected.Id,
                        selected.Revision,
                        input,
                        cancellationToken)
                    .ConfigureAwait(true)
                : await _announcements.AddAsync(input, cancellationToken).ConfigureAwait(true);
            Replace(AnnouncementMessages, state.Feed.Payload.Messages);
            SelectedAnnouncement = AnnouncementMessages.Single(message =>
                message.Id.Equals(input.Id.Trim(), StringComparison.Ordinal));
            StatusText = $"公告 {SelectedAnnouncement.Id} 修订 {SelectedAnnouncement.Revision} 已提交";
        });

    public Task WithdrawSelectedAnnouncementAsync() => SelectedAnnouncement is not { } selected
        ? Task.CompletedTask
        : RunAsync(
            "正在撤下公告…",
            async cancellationToken =>
            {
                var state = await _announcements.WithdrawAsync(
                    selected.Id,
                    selected.Revision,
                    cancellationToken).ConfigureAwait(true);
                Replace(AnnouncementMessages, state.Feed.Payload.Messages);
                NewAnnouncement();
                StatusText = $"公告 {selected.Id} 已撤下";
            });

    public string? CreateWithdrawalPreview() => SelectedAnnouncement is not { } selected
        ? null
        : $"将从公开公告源撤下：{selected.Title}\n" +
          $"公告 ID：{selected.Id}\n修订：{selected.Revision}\n\n客户端下次刷新后将不再收到该公告。";

    public void Cancel() => _operationCancellation?.Cancel();

    public void ReportError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StatusText = $"操作失败：{exception.Message}";
    }

    public void Dispose()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _announcements.Dispose();
    }

    private async Task RunAsync(
        string initialStatus,
        Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = initialStatus;
        try
        {
            await operation(_operationCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "操作已取消";
        }
        finally
        {
            IsBusy = false;
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private async Task ReloadReleasesWithinOperationAsync(CancellationToken cancellationToken)
    {
        var releases = await _github.ListReleasesAsync(cancellationToken).ConfigureAwait(true);
        Replace(
            Releases,
            releases.OrderByDescending(static release =>
                release.PublishedAtUtc ?? release.CreatedAtUtc));
        SelectedRelease = Releases.FirstOrDefault();
    }

    private AnnouncementEditorInput CreateAnnouncementInput() => new(
        AnnouncementId,
        AnnouncementTitle,
        AnnouncementBody,
        AnnouncementKind,
        ParseUtc(AnnouncementStartsAtUtc, "开始时间"),
        ParseOptionalUtc(AnnouncementEndsAtUtc, "结束时间"),
        MinimumVersion,
        MaximumVersion);

    private void LoadAnnouncementEditor(AnnouncementMessage? message)
    {
        AnnouncementId = message?.Id ?? string.Empty;
        AnnouncementTitle = message?.Title ?? string.Empty;
        AnnouncementBody = message?.Body ?? string.Empty;
        AnnouncementKind = message?.Kind ?? AnnouncementKind.Information;
        AnnouncementStartsAtUtc = FormatUtc(message?.StartsAtUtc ?? DateTimeOffset.UtcNow);
        AnnouncementEndsAtUtc = message?.EndsAtUtc is { } end ? FormatUtc(end) : string.Empty;
        MinimumVersion = message?.MinimumVersion ?? string.Empty;
        MaximumVersion = message?.MaximumVersion ?? string.Empty;
    }

    private static DateTimeOffset ParseUtc(string value, string fieldName)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result))
        {
            throw new FormatException($"{fieldName}格式无效，请使用 ISO 8601 UTC 时间。");
        }

        return result;
    }

    private static DateTimeOffset? ParseOptionalUtc(string value, string fieldName) =>
        string.IsNullOrWhiteSpace(value) ? null : ParseUtc(value, fieldName);

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}
