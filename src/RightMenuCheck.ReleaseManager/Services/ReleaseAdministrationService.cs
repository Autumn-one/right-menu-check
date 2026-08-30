using RightMenuCheck.ReleaseManager.GitHub;

namespace RightMenuCheck.ReleaseManager.Services;

public sealed record ReleaseDeletionImpact(
    long ReleaseId,
    string ExactTag,
    string ReleaseName,
    IReadOnlyList<GitHubReleaseAsset> Assets,
    bool DeleteTag)
{
    public string CreatePreview()
    {
        var totalBytes = Assets.Sum(static asset => asset.SizeBytes);
        var tagEffect = DeleteTag
            ? $"\n同时删除精确 tag：{ExactTag}"
            : "\n保留对应 Git tag";
        return $"将删除 GitHub Release：{ReleaseName}\n" +
               $"Release ID：{ReleaseId}\n" +
               $"资产：{Assets.Count} 个，共 {FormatBytes(totalBytes)}" +
               tagEffect +
               "\n\n该操作不会删除其他版本，但无法撤销。";
    }

    private static string FormatBytes(long value) => value switch
    {
        >= 1024L * 1024L * 1024L => $"{value / (1024d * 1024d * 1024d):F2} GB",
        >= 1024L * 1024L => $"{value / (1024d * 1024d):F2} MB",
        >= 1024L => $"{value / 1024d:F1} KB",
        _ => $"{value} B",
    };
}

public sealed record ReleaseDeletionResult(
    long ReleaseId,
    string ExactTag,
    bool ReleaseDeleted,
    bool TagDeleted);

public sealed class ReleaseAdministrationService
{
    private readonly IGitHubRepositoryClient _client;

    public ReleaseAdministrationService(IGitHubRepositoryClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public static ReleaseDeletionImpact PreviewDeletion(
        GitHubRelease release,
        bool deleteTag)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(release.Id);

        var tag = GitReferenceValidator.ValidateTag(release.TagName);
        return new ReleaseDeletionImpact(
            release.Id,
            tag,
            release.Name,
            release.Assets.ToArray(),
            deleteTag);
    }

    public async Task<ReleaseDeletionResult> DeleteAsync(
        ReleaseDeletionImpact confirmedImpact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmedImpact);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(confirmedImpact.ReleaseId);

        var exactTag = GitReferenceValidator.ValidateTag(confirmedImpact.ExactTag);
        await _client.DeleteReleaseAsync(
            confirmedImpact.ReleaseId,
            cancellationToken).ConfigureAwait(false);
        if (!confirmedImpact.DeleteTag)
        {
            return new ReleaseDeletionResult(
                confirmedImpact.ReleaseId,
                exactTag,
                ReleaseDeleted: true,
                TagDeleted: false);
        }

        await _client.DeleteTagAsync(exactTag, cancellationToken).ConfigureAwait(false);
        return new ReleaseDeletionResult(
            confirmedImpact.ReleaseId,
            exactTag,
            ReleaseDeleted: true,
            TagDeleted: true);
    }
}
