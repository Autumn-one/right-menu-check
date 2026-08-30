using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace RightMenuCheck.ReleaseManager.Tests.TestSupport;

internal sealed record RecordedHttpRequest(
    HttpMethod Method,
    Uri Uri,
    AuthenticationHeaderValue? Authorization,
    string? ContentType,
    byte[] Body);

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<RecordedHttpRequest, HttpResponseMessage> _responseFactory;

    public RecordingHttpMessageHandler(
        Func<RecordedHttpRequest, HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public List<RecordedHttpRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var recorded = new RecordedHttpRequest(
            request.Method,
            request.RequestUri ?? throw new InvalidOperationException("Request URI is missing."),
            request.Headers.Authorization,
            request.Content?.Headers.ContentType?.MediaType,
            body);
        Requests.Add(recorded);
        return _responseFactory(recorded);
    }

    public static HttpResponseMessage Json(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
}
