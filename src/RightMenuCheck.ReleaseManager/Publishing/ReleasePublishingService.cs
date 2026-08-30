using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RightMenuCheck.Distribution;
using RightMenuCheck.ReleaseManager.Configuration;
using RightMenuCheck.ReleaseManager.GitHub;
using RightMenuCheck.ReleaseManager.Services;

namespace RightMenuCheck.ReleaseManager.Publishing;

public enum ReleasePublishingStage
{
    Validating,
    Building,
    Packaging,
    CheckingManifest,
    CreatingRelease,
    UploadingPackage,
    PublishingManifest,
    Completed,
}

public sealed record ReleasePublishingProgress(ReleasePublishingStage Stage, string Message);

public sealed record ReleasePublishingRequest(
    string Version,
    string ReleaseNotes,
    bool IsPrerelease);

public sealed record ReleasePublishingResult(
    GitHubRelease Release,
    GitHubReleaseAsset UploadedAsset,
    ReleaseArtifact Artifact,
    SignedUpdateManifest Manifest);

public sealed class ReleasePublishingService
{
    public const string UpdateManifestPath = "distribution/update.json";
    private static readonly TimeSpan ManifestLifetime = TimeSpan.FromDays(365);
    private readonly string _repositoryRoot;
    private readonly ReleaseManagerConfiguration _configuration;
    private readonly IGitHubRepositoryClient _github;
    private readonly IPublishScriptRunner _scriptRunner;
    private readonly IReleaseArtifactBuilder _artifactBuilder;
    private readonly IDistributionSigningKeyProvider _signingKeyProvider;
    private readonly TimeProvider _timeProvider;

    public ReleasePublishingService(
        string repositoryRoot,
        ReleaseManagerConfiguration configuration,
        IGitHubRepositoryClient github,
        IPublishScriptRunner scriptRunner,
        IReleaseArtifactBuilder artifactBuilder,
        IDistributionSigningKeyProvider signingKeyProvider,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _scriptRunner = scriptRunner ?? throw new ArgumentNullException(nameof(scriptRunner));
        _artifactBuilder = artifactBuilder ?? throw new ArgumentNullException(nameof(artifactBuilder));
        _signingKeyProvider = signingKeyProvider ??
                              throw new ArgumentNullException(nameof(signingKeyProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool SupportsVersionArgument => _scriptRunner.SupportsVersionArgument;

    public async Task<ReleasePublishingResult> PublishAsync(
        ReleasePublishingRequest request,
        IProgress<ReleasePublishingProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress?.Report(new ReleasePublishingProgress(
            ReleasePublishingStage.Validating,
            "正在校验版本、签名密钥和发布目录…"));
        var version = SemanticVersion.Parse(request.Version.Trim());
        var versionText = version.ToString();
        var tag = $"v{versionText}";
        _ = GitReferenceValidator.ValidateTag(tag);
        var privateKey = _signingKeyProvider.ReadPrivateKey();
        try
        {
            _ = DistributionSignature.Sign($"release-manager-preflight:{tag}", privateKey);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new InvalidDataException("分发签名私钥无效。", exception);
        }

        var scriptPath = Path.Combine(_repositoryRoot, "scripts", "publish.ps1");
        var publishDirectory = Path.Combine(
            _repositoryRoot,
            "artifacts",
            "publish",
            "RightMenuCheck");
        progress?.Report(new ReleasePublishingProgress(
            ReleasePublishingStage.Building,
            _scriptRunner.SupportsVersionArgument
                ? $"正在构建 {versionText}…"
                : $"正在构建并核对产物版本 {versionText}…"));
        var scriptResult = await _scriptRunner.RunAsync(
            new PublishScriptRequest(
                _repositoryRoot,
                version,
                scriptPath,
                publishDirectory),
            cancellationToken).ConfigureAwait(false);
        if (!scriptResult.VersionArgumentApplied)
        {
            throw new InvalidOperationException("发布脚本未应用请求的版本参数，已停止发布。");
        }

        EnsurePublishedVersion(scriptResult.OutputDirectory, versionText);

        progress?.Report(new ReleasePublishingProgress(
            ReleasePublishingStage.Packaging,
            "正在压缩发布目录并计算 SHA-256…"));
        var artifact = await _artifactBuilder.BuildAsync(
            scriptResult.OutputDirectory,
            Path.Combine(_repositoryRoot, "artifacts", "release-manager"),
            versionText,
            cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        progress?.Report(new ReleasePublishingProgress(
            ReleasePublishingStage.CheckingManifest,
            "正在核对远程更新清单的版本和发布序列…"));
        var manifestState = await LoadManifestStateAsync(
            version,
            privateKey,
            now,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new ReleasePublishingProgress(
            ReleasePublishingStage.CreatingRelease,
            "正在创建 GitHub Release…"));
        var release = await _github.CreateReleaseAsync(
            new CreateGitHubReleaseRequest(
                tag,
                $"RightMenuCheck {versionText}",
                request.ReleaseNotes?.Trim() ?? string.Empty,
                _configuration.DefaultBranch,
                IsDraft: false,
                request.IsPrerelease),
            cancellationToken).ConfigureAwait(false);

        try
        {
            progress?.Report(new ReleasePublishingProgress(
                ReleasePublishingStage.UploadingPackage,
                $"正在上传 {artifact.AssetName}…"));
            var uploadedAsset = await _github.UploadReleaseAssetAsync(
                release.Id,
                artifact.ArchivePath,
                "application/zip",
                cancellationToken).ConfigureAwait(false);
            if (!uploadedAsset.Name.Equals(artifact.AssetName, StringComparison.Ordinal) ||
                uploadedAsset.SizeBytes != artifact.SizeBytes)
            {
                throw new InvalidDataException(
                    "GitHub 返回的发布资产名称或大小与本地压缩包不一致。");
            }

            var candidates = DistributionEndpoints.BuildReleaseDownloadCandidates(
                _configuration.Repository,
                tag,
                artifact.AssetName,
                _configuration.MirrorPrefixes);
            var directUrl = candidates[^1];
            var manifest = SignedUpdateManifest.Create(
                new UpdateManifestPayload(
                    manifestState.NextSequence,
                    now,
                    now.Add(ManifestLifetime),
                    versionText,
                    new UpdatePackage(
                        artifact.AssetName,
                        artifact.SizeBytes,
                        artifact.Sha256,
                        directUrl,
                        candidates.Take(candidates.Count - 1).ToArray()),
                    request.ReleaseNotes?.Trim() ?? string.Empty,
                    release.HtmlUrl),
                privateKey);
            progress?.Report(new ReleasePublishingProgress(
                ReleasePublishingStage.PublishingManifest,
                "正在提交已签名的 distribution/update.json…"));
            await _github.PutFileAsync(
                new PutGitHubRepositoryFileRequest(
                    UpdateManifestPath,
                    _configuration.DefaultBranch,
                    $"Publish update manifest for {tag}",
                    Encoding.UTF8.GetBytes(DistributionJson.Serialize(manifest, writeIndented: true)),
                    manifestState.RepositorySha),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new ReleasePublishingProgress(
                ReleasePublishingStage.Completed,
                $"{tag} 已发布，更新清单已提交。"));
            return new ReleasePublishingResult(release, uploadedAsset, artifact, manifest);
        }
        catch (Exception exception)
        {
            throw new RemoteReleaseIncompleteException(release.Id, release.TagName, exception);
        }
    }

    private async Task<UpdateManifestState> LoadManifestStateAsync(
        SemanticVersion requestedVersion,
        string privateKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var file = await _github.GetFileAsync(
            UpdateManifestPath,
            _configuration.DefaultBranch,
            cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return new UpdateManifestState(NextSequence: 1, RepositorySha: null);
        }

        SignedUpdateManifest current;
        try
        {
            current = DistributionJson.Deserialize<SignedUpdateManifest>(
                Encoding.UTF8.GetString(file.Content));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new InvalidDataException("远程更新清单格式无效，已停止发布。", exception);
        }

        if (!SemanticVersion.TryParse(current.Payload.Version, out var currentVersion))
        {
            throw new InvalidDataException("远程更新清单版本无效，已停止发布。");
        }

        var currentDecision = UpdatePolicyEvaluator.Evaluate(
            currentVersion,
            current,
            privateKey,
            now);
        if (currentDecision.Kind == UpdateDecisionKind.InvalidManifest)
        {
            throw new InvalidDataException("远程更新清单签名或内容无效，已停止发布。");
        }

        if (requestedVersion <= currentVersion)
        {
            throw new InvalidOperationException(
                $"远程更新清单版本为 {currentVersion}，新发布版本必须更高，已停止回退覆盖。");
        }

        try
        {
            return new UpdateManifestState(
                checked(current.Payload.Sequence + 1),
                file.Sha);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("远程更新清单序列已耗尽，无法安全发布。", exception);
        }
    }

    private static void EnsurePublishedVersion(string outputDirectory, string expectedVersion)
    {
        var buildInfoPath = Path.Combine(outputDirectory, "build-info.json");
        if (!File.Exists(buildInfoPath))
        {
            throw new FileNotFoundException("发布目录缺少 build-info.json。", buildInfoPath);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(buildInfoPath));
            if (!document.RootElement.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("build-info.json 缺少字符串版本号。");
            }

            var actualVersion = versionElement.GetString();
            if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"产物版本为 {actualVersion ?? "<缺失>"}，不能按 {expectedVersion} 发布。" +
                    "请先让 publish.ps1 生成匹配版本的二进制。");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("build-info.json 格式无效。", exception);
        }
    }

    private sealed record UpdateManifestState(long NextSequence, string? RepositorySha);
}

public sealed class RemoteReleaseIncompleteException : Exception
{
    public RemoteReleaseIncompleteException(long releaseId, string tag, Exception innerException)
        : base(
            $"GitHub Release {tag}（ID {releaseId}）已创建，但后续上传或控制文件提交失败。" +
            "请在发布历史中检查并修复该版本。",
            innerException)
    {
        ReleaseId = releaseId;
        Tag = tag;
    }

    public long ReleaseId { get; }

    public string Tag { get; }
}
