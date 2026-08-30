using System.Text;
using RightMenuCheck.Distribution;
using RightMenuCheck.ReleaseManager.Configuration;
using RightMenuCheck.ReleaseManager.GitHub;
using RightMenuCheck.ReleaseManager.Services;

namespace RightMenuCheck.ReleaseManager.Announcements;

public sealed record AnnouncementEditorInput(
    string Id,
    string Title,
    string Body,
    AnnouncementKind Kind,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    string? MinimumVersion,
    string? MaximumVersion);

public sealed record AnnouncementFeedState(
    SignedAnnouncementFeed Feed,
    string? RepositorySha);

public sealed class AnnouncementManagementService : IDisposable
{
    public const string AnnouncementPath = "distribution/messages.json";
    private static readonly TimeSpan FeedLifetime = TimeSpan.FromDays(365);
    private readonly ReleaseManagerConfiguration _configuration;
    private readonly IGitHubRepositoryClient _github;
    private readonly IDistributionSigningKeyProvider _keyProvider;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    public AnnouncementManagementService(
        ReleaseManagerConfiguration configuration,
        IGitHubRepositoryClient github,
        IDistributionSigningKeyProvider keyProvider,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AnnouncementFeedState> LoadAsync(CancellationToken cancellationToken)
    {
        var file = await _github.GetFileAsync(
            AnnouncementPath,
            _configuration.DefaultBranch,
            cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            var now = _timeProvider.GetUtcNow();
            return new AnnouncementFeedState(
                SignedAnnouncementFeed.Create(
                    new AnnouncementFeedPayload(
                        Sequence: 1,
                        IssuedAtUtc: now,
                        ExpiresAtUtc: now.Add(FeedLifetime),
                        Messages: []),
                    _keyProvider.ReadPrivateKey()),
                RepositorySha: null);
        }

        SignedAnnouncementFeed feed;
        try
        {
            feed = DistributionJson.Deserialize<SignedAnnouncementFeed>(
                Encoding.UTF8.GetString(file.Content));
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException)
        {
            throw new InvalidDataException("远程公告文件格式无效，已停止覆盖。", exception);
        }

        var privateKey = _keyProvider.ReadPrivateKey();
        if (!feed.HasValidSignature(privateKey))
        {
            throw new InvalidDataException("远程公告签名无效，已停止覆盖。");
        }

        var currentTime = _timeProvider.GetUtcNow();
        if (feed.Payload.Sequence <= 0 ||
            feed.Payload.ExpiresAtUtc <= feed.Payload.IssuedAtUtc ||
            feed.Payload.IssuedAtUtc > currentTime.AddMinutes(5))
        {
            throw new InvalidDataException("远程公告序列或有效期无效，已停止覆盖。");
        }

        return new AnnouncementFeedState(feed, file.Sha);
    }

    public async Task<AnnouncementFeedState> AddAsync(
        AnnouncementEditorInput input,
        CancellationToken cancellationToken)
    {
        ValidateInput(input);
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state.Feed.Payload.Messages.Any(message =>
                    message.Id.Equals(input.Id, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("公告 ID 已存在，请使用修订操作。");
            }

            var messages = state.Feed.Payload.Messages
                .Append(ToMessage(input, revision: 1))
                .ToArray();
            return await SaveAsync(messages, state, "Add announcement", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<AnnouncementFeedState> ReviseAsync(
        string exactId,
        int expectedRevision,
        AnnouncementEditorInput input,
        CancellationToken cancellationToken)
    {
        ValidateInput(input);
        if (!input.Id.Equals(exactId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("修订不能更改公告 ID。");
        }

        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var current = state.Feed.Payload.Messages.SingleOrDefault(message =>
                message.Id.Equals(exactId, StringComparison.Ordinal));
            if (current is null || current.Revision != expectedRevision)
            {
                throw new InvalidOperationException("远程公告已变化，请刷新后重试。");
            }

            var replacement = ToMessage(input, checked(current.Revision + 1));
            var messages = state.Feed.Payload.Messages
                .Select(message => message.Id.Equals(exactId, StringComparison.Ordinal)
                    ? replacement
                    : message)
                .ToArray();
            return await SaveAsync(
                    messages,
                    state,
                    $"Revise announcement {exactId} to {replacement.Revision}",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<AnnouncementFeedState> WithdrawAsync(
        string exactId,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        ValidateId(exactId);
        await _mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var current = state.Feed.Payload.Messages.SingleOrDefault(message =>
                message.Id.Equals(exactId, StringComparison.Ordinal));
            if (current is null || current.Revision != expectedRevision)
            {
                throw new InvalidOperationException("远程公告已变化，请刷新后重试。");
            }

            var messages = state.Feed.Payload.Messages
                .Where(message => !message.Id.Equals(exactId, StringComparison.Ordinal))
                .ToArray();
            return await SaveAsync(
                    messages,
                    state,
                    $"Withdraw announcement {exactId}",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public void Dispose() => _mutationLock.Dispose();

    private async Task<AnnouncementFeedState> SaveAsync(
        IReadOnlyList<AnnouncementMessage> messages,
        AnnouncementFeedState previousState,
        string commitMessage,
        CancellationToken cancellationToken)
    {
        var orderedMessages = messages
            .OrderBy(static message => message.StartsAtUtc)
            .ThenBy(static message => message.Id, StringComparer.Ordinal)
            .ToArray();
        var now = _timeProvider.GetUtcNow();
        var sequence = GetNextSequence(previousState);
        var feed = SignedAnnouncementFeed.Create(
            new AnnouncementFeedPayload(
                sequence,
                now,
                now.Add(FeedLifetime),
                orderedMessages),
            _keyProvider.ReadPrivateKey());
        var bytes = Encoding.UTF8.GetBytes(DistributionJson.Serialize(feed, writeIndented: true));
        var saved = await _github.PutFileAsync(
            new PutGitHubRepositoryFileRequest(
                AnnouncementPath,
                _configuration.DefaultBranch,
                commitMessage,
                bytes,
                previousState.RepositorySha),
            cancellationToken).ConfigureAwait(false);
        return new AnnouncementFeedState(feed, saved.Sha);
    }

    private static long GetNextSequence(AnnouncementFeedState previousState)
    {
        if (previousState.RepositorySha is null)
        {
            return 1;
        }

        try
        {
            return checked(previousState.Feed.Payload.Sequence + 1);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("远程公告序列已耗尽，无法安全发布。", exception);
        }
    }

    private static AnnouncementMessage ToMessage(AnnouncementEditorInput input, int revision) => new(
        input.Id.Trim(),
        revision,
        input.Title.Trim(),
        input.Body.Trim(),
        input.Kind,
        input.StartsAtUtc.ToUniversalTime(),
        input.EndsAtUtc?.ToUniversalTime(),
        NormalizeOptionalVersion(input.MinimumVersion),
        NormalizeOptionalVersion(input.MaximumVersion));

    private static void ValidateInput(AnnouncementEditorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateId(input.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Body);
        if (input.EndsAtUtc is { } end && end <= input.StartsAtUtc)
        {
            throw new ArgumentException("公告结束时间必须晚于开始时间。", nameof(input));
        }

        var minimum = ParseOptionalVersion(input.MinimumVersion, nameof(input.MinimumVersion));
        var maximum = ParseOptionalVersion(input.MaximumVersion, nameof(input.MaximumVersion));
        if (minimum is { } min && maximum is { } max && min > max)
        {
            throw new ArgumentException("最低版本不能高于最高版本。", nameof(input));
        }
    }

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var value = id.Trim();
        if (value.Length > 64 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("公告 ID 只能包含字母、数字、点、连字符和下划线。", nameof(id));
        }
    }

    private static SemanticVersion? ParseOptionalVersion(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!SemanticVersion.TryParse(value.Trim(), out var version))
        {
            throw new ArgumentException("公告版本范围格式无效。", parameterName);
        }

        return version;
    }

    private static string? NormalizeOptionalVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : SemanticVersion.Parse(value.Trim()).ToString();
}
