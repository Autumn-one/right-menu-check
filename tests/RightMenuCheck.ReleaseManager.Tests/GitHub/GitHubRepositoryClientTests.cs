using System.Net;
using System.Text;
using System.Text.Json;
using RightMenuCheck.Distribution;
using RightMenuCheck.ReleaseManager.GitHub;
using RightMenuCheck.ReleaseManager.Tests.TestSupport;

namespace RightMenuCheck.ReleaseManager.Tests.GitHub;

public sealed class GitHubRepositoryClientTests
{
    private const string AccessToken = "not-for-output-secret";

    [Fact]
    public async Task ListsAndCreatesReleasesUsingExpectedRepositoryEndpoints()
    {
        var releaseJson = CreateReleaseJson(42, "v1.2.3");
        var handler = new RecordingHttpMessageHandler(request =>
            RecordingHttpMessageHandler.Json(request.Method == HttpMethod.Get
                ? $"[{releaseJson}]"
                : releaseJson, request.Method == HttpMethod.Post
                    ? HttpStatusCode.Created
                    : HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var releases = await client.ListReleasesAsync(CancellationToken.None);
        var created = await client.CreateReleaseAsync(
            new CreateGitHubReleaseRequest(
                "v1.2.3",
                "RightMenuCheck 1.2.3",
                "Notes",
                "main",
                IsDraft: false,
                IsPrerelease: false),
            CancellationToken.None);

        Assert.Equal(42, Assert.Single(releases).Id);
        Assert.Equal("v1.2.3", created.TagName);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(
                    "https://api.github.com/repos/owner/repo/releases?per_page=100&page=1",
                    request.Uri.AbsoluteUri);
                Assert.Equal("Bearer", request.Authorization?.Scheme);
                Assert.Equal(AccessToken, request.Authorization?.Parameter);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "https://api.github.com/repos/owner/repo/releases",
                    request.Uri.AbsoluteUri);
                using var document = JsonDocument.Parse(request.Body);
                Assert.Equal("v1.2.3", document.RootElement.GetProperty("tag_name").GetString());
                Assert.Equal("main", document.RootElement.GetProperty("target_commitish").GetString());
            });
    }

    [Fact]
    public async Task UploadsAssetAndDeletesOnlyExactReleaseAndTag()
    {
        using var directory = new TemporaryDirectory();
        var assetPath = Path.Combine(directory.Path, "RightMenuCheck-1.2.3-win-x64.zip");
        await File.WriteAllBytesAsync(assetPath, [1, 2, 3, 4]);
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.Uri.Host.Equals("uploads.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return RecordingHttpMessageHandler.Json(
                    """
                    {
                      "id": 9,
                      "name": "RightMenuCheck-1.2.3-win-x64.zip",
                      "size": 4,
                      "browser_download_url": "https://github.com/owner/repo/releases/download/v1.2.3/RightMenuCheck-1.2.3-win-x64.zip",
                      "content_type": "application/zip",
                      "download_count": 0
                    }
                    """,
                    HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var asset = await client.UploadReleaseAssetAsync(
            42,
            assetPath,
            "application/zip",
            CancellationToken.None);
        await client.DeleteReleaseAsync(42, CancellationToken.None);
        await client.DeleteTagAsync("v1.2.3/hotfix", CancellationToken.None);

        Assert.Equal(9, asset.Id);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(
                    "https://uploads.github.com/repos/owner/repo/releases/42/assets?name=RightMenuCheck-1.2.3-win-x64.zip",
                    request.Uri.AbsoluteUri);
                Assert.Equal("application/zip", request.ContentType);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, request.Body);
            },
            request => Assert.Equal(
                "https://api.github.com/repos/owner/repo/releases/42",
                request.Uri.AbsoluteUri),
            request => Assert.Equal(
                "https://api.github.com/repos/owner/repo/git/refs/tags/v1.2.3/hotfix",
                request.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task GetsAndUpdatesContentWithExactBranchAndSha()
    {
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return RecordingHttpMessageHandler.Json(
                    """
                    {
                      "path": "distribution/update.json",
                      "sha": "old-sha",
                      "content": "eyJvbGQiOnRydWV9",
                      "encoding": "base64"
                    }
                    """);
            }

            return RecordingHttpMessageHandler.Json(
                """
                {
                  "content": {
                    "path": "distribution/update.json",
                    "sha": "new-sha",
                    "content": "",
                    "encoding": "base64"
                  }
                }
                """,
                HttpStatusCode.Created);
        });
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var existing = await client.GetFileAsync(
            "distribution/update.json",
            "main",
            CancellationToken.None);
        var saved = await client.PutFileAsync(
            new PutGitHubRepositoryFileRequest(
                "distribution/update.json",
                "main",
                "Update manifest",
                Encoding.UTF8.GetBytes("{\"new\":true}"),
                existing?.Sha),
            CancellationToken.None);

        Assert.Equal("{\"old\":true}", Encoding.UTF8.GetString(Assert.IsType<byte[]>(existing?.Content)));
        Assert.Equal("new-sha", saved.Sha);
        Assert.Equal(
            "https://api.github.com/repos/owner/repo/contents/distribution/update.json?ref=main",
            handler.Requests[0].Uri.AbsoluteUri);
        using var payload = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("old-sha", payload.RootElement.GetProperty("sha").GetString());
        Assert.Equal("main", payload.RootElement.GetProperty("branch").GetString());
        Assert.Equal(
            "{\"new\":true}",
            Encoding.UTF8.GetString(Convert.FromBase64String(
                payload.RootElement.GetProperty("content").GetString()!)));
    }

    [Fact]
    public async Task ApiFailureDoesNotIncludeTokenOrResponseBody()
    {
        const string responseMarker = "server-secret-echo";
        var handler = new RecordingHttpMessageHandler(_ =>
            RecordingHttpMessageHandler.Json(
                $"{{\"message\":\"{responseMarker}:{AccessToken}\"}}",
                HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.ListReleasesAsync(CancellationToken.None));

        Assert.DoesNotContain(AccessToken, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(responseMarker, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRepositoryFileReturnsNullWithoutFallbackRequest()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);
        var client = CreateClient(httpClient);

        var result = await client.GetFileAsync(
            "distribution/messages.json",
            "main",
            CancellationToken.None);

        Assert.Null(result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://api.github.com/repos/owner/repo/contents/distribution/messages.json?ref=main",
            request.Uri.AbsoluteUri);
    }

    private static GitHubRepositoryClient CreateClient(HttpClient httpClient) => new(
        httpClient,
        RepositoryCoordinates.Parse("owner/repo"),
        AccessToken);

    private static string CreateReleaseJson(long id, string tag) => $$"""
        {
          "id": {{id}},
          "tag_name": "{{tag}}",
          "name": "RightMenuCheck {{tag}}",
          "body": "Notes",
          "draft": false,
          "prerelease": false,
          "created_at": "2026-08-31T01:00:00Z",
          "published_at": "2026-08-31T01:01:00Z",
          "html_url": "https://github.com/owner/repo/releases/tag/{{tag}}",
          "assets": []
        }
        """;
}
