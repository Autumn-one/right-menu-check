using RightMenuCheck.ReleaseManager.GitHub;

namespace RightMenuCheck.ReleaseManager.Tests.TestSupport;

internal sealed class FakeGitHubRepositoryClient : IGitHubRepositoryClient
{
    private int _shaSequence;

    public List<string> Calls { get; } = [];

    public List<GitHubRelease> Releases { get; } = [];

    public GitHubRepositoryFile? RepositoryFile { get; set; }

    public PutGitHubRepositoryFileRequest? LastPutRequest { get; private set; }

    public List<PutGitHubRepositoryFileRequest> PutRequests { get; } = [];

    public CreateGitHubReleaseRequest? LastCreateRequest { get; private set; }

    public long? DeletedReleaseId { get; private set; }

    public string? DeletedTag { get; private set; }

    public Func<CreateGitHubReleaseRequest, GitHubRelease>? CreateReleaseFactory { get; set; }

    public Exception? UploadException { get; set; }

    public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("list-releases");
        return Task.FromResult<IReadOnlyList<GitHubRelease>>(Releases.ToArray());
    }

    public Task<GitHubRelease> CreateReleaseAsync(
        CreateGitHubReleaseRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("create-release");
        LastCreateRequest = request;
        var release = CreateReleaseFactory?.Invoke(request) ?? new GitHubRelease(
            501,
            request.TagName,
            request.Name,
            request.Body,
            request.IsDraft,
            request.IsPrerelease,
            DateTimeOffset.Parse("2026-08-31T01:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-08-31T01:00:00Z", CultureInfo.InvariantCulture),
            $"https://github.com/owner/repo/releases/tag/{request.TagName}",
            []);
        Releases.Add(release);
        return Task.FromResult(release);
    }

    public Task<GitHubReleaseAsset> UploadReleaseAssetAsync(
        long releaseId,
        string assetPath,
        string contentType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add($"upload:{releaseId}");
        if (UploadException is not null)
        {
            throw UploadException;
        }

        var information = new FileInfo(assetPath);
        return Task.FromResult(new GitHubReleaseAsset(
            701,
            information.Name,
            information.Length,
            $"https://github.com/owner/repo/releases/download/v0.1.0/{information.Name}",
            contentType,
            0));
    }

    public Task DeleteReleaseAsync(long releaseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add($"delete-release:{releaseId}");
        DeletedReleaseId = releaseId;
        return Task.CompletedTask;
    }

    public Task DeleteTagAsync(string exactTag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add($"delete-tag:{exactTag}");
        DeletedTag = exactTag;
        return Task.CompletedTask;
    }

    public Task<GitHubRepositoryFile?> GetFileAsync(
        string path,
        string branch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add($"get-file:{path}:{branch}");
        return Task.FromResult(RepositoryFile);
    }

    public Task<GitHubRepositoryFile> PutFileAsync(
        PutGitHubRepositoryFileRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add($"put-file:{request.Path}:{request.Branch}");
        LastPutRequest = request;
        PutRequests.Add(request);
        RepositoryFile = new GitHubRepositoryFile(
            request.Path,
            $"sha-{++_shaSequence}",
            request.Content.ToArray());
        return Task.FromResult(RepositoryFile);
    }
}
