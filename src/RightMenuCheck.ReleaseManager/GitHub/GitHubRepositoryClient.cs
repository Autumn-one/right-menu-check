using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.ReleaseManager.GitHub;

public sealed class GitHubRepositoryClient : IGitHubRepositoryClient
{
    private const int PageSize = 100;
    private const int MaximumPages = 100;
    private readonly HttpClient _httpClient;
    private readonly RepositoryCoordinates _repository;
    private readonly string _accessToken;

    public GitHubRepositoryClient(
        HttpClient httpClient,
        RepositoryCoordinates repository,
        string accessToken)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        _accessToken = accessToken.Trim();
    }

    public async Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(
        CancellationToken cancellationToken)
    {
        var releases = new List<GitHubRelease>();
        for (var page = 1; page <= MaximumPages; page++)
        {
            using var response = await SendAsync(
                HttpMethod.Get,
                ApiUri($"releases?per_page={PageSize}&page={page}"),
                content: null,
                "读取发布历史",
                cancellationToken).ConfigureAwait(false);
            var pageItems = await DeserializeAsync<GitHubReleaseDto[]>(
                response,
                cancellationToken).ConfigureAwait(false);
            releases.AddRange(pageItems.Select(MapRelease));
            if (pageItems.Length < PageSize)
            {
                return releases;
            }
        }

        throw new InvalidOperationException("GitHub 发布历史超过安全分页上限。");
    }

    public async Task<GitHubRelease> CreateReleaseAsync(
        CreateGitHubReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tag = GitReferenceValidator.ValidateTag(request.TagName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetCommitish);
        var payload = new CreateReleaseDto(
            tag,
            request.Name.Trim(),
            request.Body ?? string.Empty,
            request.TargetCommitish.Trim(),
            request.IsDraft,
            request.IsPrerelease);
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            HttpMethod.Post,
            ApiUri("releases"),
            content,
            "创建发布版本",
            cancellationToken).ConfigureAwait(false);
        return MapRelease(await DeserializeAsync<GitHubReleaseDto>(
            response,
            cancellationToken).ConfigureAwait(false));
    }

    public async Task<GitHubReleaseAsset> UploadReleaseAssetAsync(
        long releaseId,
        string assetPath,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(releaseId);

        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var fullPath = Path.GetFullPath(assetPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("待上传发布资产不存在。", fullPath);
        }

        var assetName = Path.GetFileName(fullPath);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        var uploadUri = new Uri(
            $"https://uploads.github.com/repos/{Escape(_repository.Owner)}/" +
            $"{Escape(_repository.Name)}/releases/{releaseId}/assets?name={Escape(assetName)}");
        using var response = await SendAsync(
            HttpMethod.Post,
            uploadUri,
            content,
            "上传发布资产",
            cancellationToken).ConfigureAwait(false);
        return MapAsset(await DeserializeAsync<GitHubAssetDto>(
            response,
            cancellationToken).ConfigureAwait(false));
    }

    public async Task DeleteReleaseAsync(long releaseId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(releaseId);

        using var response = await SendAsync(
            HttpMethod.Delete,
            ApiUri($"releases/{releaseId}"),
            content: null,
            "删除指定发布版本",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTagAsync(string exactTag, CancellationToken cancellationToken)
    {
        var tag = GitReferenceValidator.ValidateTag(exactTag);
        using var response = await SendAsync(
            HttpMethod.Delete,
            ApiUri($"git/refs/tags/{EscapePath(tag)}"),
            content: null,
            "删除指定 Git tag",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubRepositoryFile?> GetFileAsync(
        string path,
        string branch,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeRepositoryPath(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        using var request = CreateRequest(
            HttpMethod.Get,
            ApiUri($"contents/{EscapePath(normalizedPath)}?ref={Escape(branch.Trim())}"),
            content: null);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureSuccess(response, "读取仓库控制文件");
        var dto = await DeserializeAsync<GitHubContentDto>(
            response,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(dto.Encoding, "base64", StringComparison.OrdinalIgnoreCase) ||
            dto.Content is null)
        {
            throw new InvalidDataException("GitHub 返回了不受支持的仓库文件编码。");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dto.Content.Replace("\n", string.Empty, StringComparison.Ordinal));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("GitHub 返回了无效的 Base64 文件内容。", exception);
        }

        return new GitHubRepositoryFile(dto.Path, dto.Sha, bytes);
    }

    public async Task<GitHubRepositoryFile> PutFileAsync(
        PutGitHubRepositoryFileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedPath = NormalizeRepositoryPath(request.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CommitMessage);
        ArgumentNullException.ThrowIfNull(request.Content);
        var payload = new PutContentDto(
            request.CommitMessage.Trim(),
            Convert.ToBase64String(request.Content),
            request.Branch.Trim(),
            string.IsNullOrWhiteSpace(request.ExistingSha) ? null : request.ExistingSha.Trim());
        using var content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await SendAsync(
            HttpMethod.Put,
            ApiUri($"contents/{EscapePath(normalizedPath)}"),
            content,
            "更新仓库控制文件",
            cancellationToken).ConfigureAwait(false);
        var dto = await DeserializeAsync<PutContentResponseDto>(
            response,
            cancellationToken).ConfigureAwait(false);
        return new GitHubRepositoryFile(dto.Content.Path, dto.Content.Sha, request.Content.ToArray());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        string operation,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, uri, content);
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSuccess(response, operation);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("RightMenuCheck-ReleaseManager/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException(response.StatusCode, operation);
        }
    }

    private static async Task<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(
                       JsonOptions,
                       cancellationToken).ConfigureAwait(false) ??
                   throw new InvalidDataException("GitHub 响应不包含 JSON 对象。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub 响应 JSON 格式无效。", exception);
        }
    }

    private Uri ApiUri(string relativePath) => new(
        $"https://api.github.com/repos/{Escape(_repository.Owner)}/" +
        $"{Escape(_repository.Name)}/{relativePath}");

    private static GitHubRelease MapRelease(GitHubReleaseDto dto) => new(
        dto.Id,
        dto.TagName,
        dto.Name ?? dto.TagName,
        dto.Body ?? string.Empty,
        dto.Draft,
        dto.Prerelease,
        dto.CreatedAt,
        dto.PublishedAt,
        dto.HtmlUrl,
        dto.Assets.Select(MapAsset).ToArray());

    private static GitHubReleaseAsset MapAsset(GitHubAssetDto dto) => new(
        dto.Id,
        dto.Name,
        dto.Size,
        dto.BrowserDownloadUrl,
        dto.ContentType,
        dto.DownloadCount);

    private static string NormalizeRepositoryPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('\\', '/').Trim('/');
        var parts = normalized.Split('/');
        if (parts.Any(static part => part.Length == 0 || part is "." or ".."))
        {
            throw new ArgumentException("仓库路径必须是规范的相对路径。", nameof(value));
        }

        return normalized;
    }

    private static string EscapePath(string value) =>
        string.Join('/', value.Split('/').Select(Escape));

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record CreateReleaseDto(
        string TagName,
        string Name,
        string Body,
        string TargetCommitish,
        bool Draft,
        bool Prerelease);

    private sealed record GitHubReleaseDto(
        long Id,
        string TagName,
        string? Name,
        string? Body,
        bool Draft,
        bool Prerelease,
        DateTimeOffset CreatedAt,
        DateTimeOffset? PublishedAt,
        string HtmlUrl,
        IReadOnlyList<GitHubAssetDto> Assets);

    private sealed record GitHubAssetDto(
        long Id,
        string Name,
        long Size,
        string BrowserDownloadUrl,
        string ContentType,
        int DownloadCount);

    private sealed record GitHubContentDto(
        string Path,
        string Sha,
        string? Content,
        string? Encoding);

    private sealed record PutContentDto(
        string Message,
        string Content,
        string Branch,
        string? Sha);

    private sealed record PutContentResponseDto(GitHubContentDto Content);
}
