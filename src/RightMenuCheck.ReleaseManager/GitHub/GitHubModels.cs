namespace RightMenuCheck.ReleaseManager.GitHub;

public sealed record GitHubReleaseAsset(
    long Id,
    string Name,
    long SizeBytes,
    string DownloadUrl,
    string ContentType,
    int DownloadCount);

public sealed record GitHubRelease(
    long Id,
    string TagName,
    string Name,
    string Body,
    bool IsDraft,
    bool IsPrerelease,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    string HtmlUrl,
    IReadOnlyList<GitHubReleaseAsset> Assets);

public sealed record CreateGitHubReleaseRequest(
    string TagName,
    string Name,
    string Body,
    string TargetCommitish,
    bool IsDraft,
    bool IsPrerelease);

public sealed record GitHubRepositoryFile(
    string Path,
    string Sha,
    byte[] Content);

public sealed record PutGitHubRepositoryFileRequest(
    string Path,
    string Branch,
    string CommitMessage,
    byte[] Content,
    string? ExistingSha);

public interface IGitHubRepositoryClient
{
    Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(CancellationToken cancellationToken);

    Task<GitHubRelease> CreateReleaseAsync(
        CreateGitHubReleaseRequest request,
        CancellationToken cancellationToken);

    Task<GitHubReleaseAsset> UploadReleaseAssetAsync(
        long releaseId,
        string assetPath,
        string contentType,
        CancellationToken cancellationToken);

    Task DeleteReleaseAsync(long releaseId, CancellationToken cancellationToken);

    Task DeleteTagAsync(string exactTag, CancellationToken cancellationToken);

    Task<GitHubRepositoryFile?> GetFileAsync(
        string path,
        string branch,
        CancellationToken cancellationToken);

    Task<GitHubRepositoryFile> PutFileAsync(
        PutGitHubRepositoryFileRequest request,
        CancellationToken cancellationToken);
}
