using System.Text;
using System.Text.Json;
using RightMenuCheck.Distribution;
using RightMenuCheck.ReleaseManager.GitHub;
using RightMenuCheck.ReleaseManager.Publishing;

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
    private readonly string _defaultBranch;
    private readonly string _publicKeyPem;

    public ReleaseAdministrationService(
        IGitHubRepositoryClient client,
        string defaultBranch,
        string publicKeyPem)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        _defaultBranch = defaultBranch.Trim();
        _publicKeyPem = publicKeyPem;
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
        await EnsureReleaseIsNotCurrentAsync(
            exactTag,
            cancellationToken).ConfigureAwait(false);
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

    private async Task EnsureReleaseIsNotCurrentAsync(
        string exactTag,
        CancellationToken cancellationToken)
    {
        var file = await _client.GetFileAsync(
            ReleasePublishingService.UpdateManifestPath,
            _defaultBranch,
            cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return;
        }

        SignedUpdateManifest manifest;
        try
        {
            manifest = DistributionJson.Deserialize<SignedUpdateManifest>(
                Encoding.UTF8.GetString(file.Content));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new InvalidDataException(
                "远程更新清单格式无效，无法安全判断该版本是否仍在使用。",
                exception);
        }

        if (!manifest.HasValidSignature(_publicKeyPem) ||
            !SemanticVersion.TryParse(manifest.Payload.Version, out var activeVersion))
        {
            throw new InvalidDataException(
                "远程更新清单签名或版本无效，无法安全删除发布版本。");
        }

        var activeTag = $"v{activeVersion}";
        if (exactTag.Equals(activeTag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{exactTag} 仍由当前更新清单引用。请先发布更高版本，再删除这个历史版本。");
        }
    }
}
