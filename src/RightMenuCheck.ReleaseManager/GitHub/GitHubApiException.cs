using System.Net;

namespace RightMenuCheck.ReleaseManager.GitHub;

public sealed class GitHubApiException : Exception
{
    public GitHubApiException(HttpStatusCode statusCode, string operation)
        : base($"GitHub 操作失败：{operation}（HTTP {(int)statusCode}）。")
    {
        StatusCode = statusCode;
        Operation = operation;
    }

    public HttpStatusCode StatusCode { get; }

    public string Operation { get; }
}
