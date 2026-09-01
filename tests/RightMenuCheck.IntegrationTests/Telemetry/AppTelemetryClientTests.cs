using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using RightMenuCheck.App.Services;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.IntegrationTests.Telemetry;

public sealed class AppTelemetryClientTests
{
    private const string MachineId =
        "4a8b9855d6f79b23c47dc40de8f65b6a00fe6262ae55cc506db87f64708ea803";
    private const string SessionTokenA =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly TimeSpan TestWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly string SessionTokenB = $"B{new string('A', 42)}";
    private static readonly string SessionTokenC = $"C{new string('A', 42)}";

    [Fact]
    public async Task StartHeartbeatAndStopUseOneSessionAndExpectedContracts()
    {
        using var handler = new RecordingTelemetryHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var clock = new ManualTelemetryClock();
        await using var client = new AppTelemetryClient(
            httpClient,
            CreateOptions(),
            new FixedIdentityProvider(MachineId),
            clock);

        await client.StartAsync();
        var start = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        var heartbeat = await handler.ReadNextAsync();
        await client.StopAsync();
        var end = await handler.ReadNextAsync();

        Assert.Equal(TelemetryEndpoints.StartPath, start.Path);
        Assert.Equal(TelemetryEndpoints.HeartbeatPath, heartbeat.Path);
        Assert.Equal(TelemetryEndpoints.EndPath, end.Path);
        Assert.Equal(HttpMethod.Post, start.Method);
        Assert.Equal("application/json", start.ContentType);
        Assert.Null(start.AuthorizationScheme);
        Assert.Equal("Bearer", heartbeat.AuthorizationScheme);
        Assert.Equal(SessionTokenA, heartbeat.AuthorizationParameter);
        Assert.Equal("Bearer", end.AuthorizationScheme);
        Assert.Equal(SessionTokenA, end.AuthorizationParameter);

        var startBody = DistributionJson.Deserialize<TelemetryStartRequest>(start.Body);
        var heartbeatBody = DistributionJson.Deserialize<TelemetryHeartbeatRequest>(heartbeat.Body);
        var endBody = DistributionJson.Deserialize<TelemetryEndRequest>(end.Body);
        Assert.Equal(MachineId, startBody.MachineId);
        Assert.Equal(startBody.SessionId, heartbeatBody.SessionId);
        Assert.Equal(startBody.SessionId, endBody.SessionId);
        Assert.Equal(MachineId, heartbeatBody.MachineId);
        Assert.Equal(MachineId, endBody.MachineId);
        Assert.True(TelemetryIdentityValidator.IsValidSessionId(startBody.SessionId));
    }

    [Fact]
    public async Task SessionEndedHeartbeatResumesWithNewAuthenticatedSegment()
    {
        using var handler = new ScriptedTelemetryHandler(
            SessionResponse(SessionTokenA, startupCount: 7),
            ErrorResponse(HttpStatusCode.Conflict, "session_ended"),
            SessionResponse(SessionTokenB, startupCount: 7),
            NoContentResponse(),
            NoContentResponse());
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var clock = new ManualTelemetryClock();
        await using var client = new AppTelemetryClient(
            httpClient,
            CreateOptions(),
            new FixedIdentityProvider(MachineId),
            clock);

        await client.StartAsync();
        var start = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        var failedHeartbeat = await handler.ReadNextAsync();
        var resume = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        var resumedHeartbeat = await handler.ReadNextAsync();
        await client.StopAsync();
        var end = await handler.ReadNextAsync();

        var startBody = DistributionJson.Deserialize<TelemetryStartRequest>(start.Body);
        var resumeBody = DistributionJson.Deserialize<TelemetryResumeRequest>(resume.Body);
        Assert.Equal(TelemetryEndpoints.HeartbeatPath, failedHeartbeat.Path);
        Assert.Equal(TelemetryEndpoints.ResumePath, resume.Path);
        Assert.Equal(startBody.SessionId, resumeBody.PreviousSessionId);
        Assert.NotEqual(startBody.SessionId, resumeBody.SessionId);
        Assert.Equal(SessionTokenA, resume.AuthorizationParameter);
        Assert.Equal(SessionTokenB, resumedHeartbeat.AuthorizationParameter);
        Assert.Equal(SessionTokenB, end.AuthorizationParameter);
        Assert.Equal(
            resumeBody.SessionId,
            DistributionJson.Deserialize<TelemetryEndRequest>(end.Body).SessionId);
    }

    [Fact]
    public async Task UnknownLengthErrorBodyIsReadOnlyThroughSafetyLimit()
    {
        var oversizedBody = Encoding.UTF8.GetBytes(
            $"{{\"code\":\"session_ended\",\"padding\":\"{new string('x', 10_000)}\"}}");
        using var responseStream = new CountingReadStream(
            oversizedBody,
            expectedBytesRead: 4097);
        var oversizedResponse = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StreamContent(responseStream),
        };
        Assert.Null(oversizedResponse.Content.Headers.ContentLength);

        using var handler = new ScriptedTelemetryHandler(
            SessionResponse(SessionTokenA, startupCount: 2),
            _ => oversizedResponse,
            NoContentResponse());
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var clock = new ManualTelemetryClock();
        await using var client = new AppTelemetryClient(
            httpClient,
            CreateOptions(),
            new FixedIdentityProvider(MachineId),
            clock);

        await client.StartAsync();
        _ = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        _ = await handler.ReadNextAsync();
        await responseStream.WaitForExpectedReadAsync();
        await client.StopAsync();
        var end = await handler.ReadNextAsync();

        Assert.Equal(4097, responseStream.BytesRead);
        Assert.Equal(TelemetryEndpoints.EndPath, end.Path);
    }

    [Fact]
    public async Task TransportInterruptionBacksOffThenResumesWithoutSensitiveLogs()
    {
        using var handler = new ScriptedTelemetryHandler(
            SessionResponse(SessionTokenA, startupCount: 3),
            _ => throw new HttpRequestException(
                $"sensitive failure {MachineId} {SessionTokenA}"),
            SessionResponse(SessionTokenB, startupCount: 3),
            NoContentResponse(),
            NoContentResponse());
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var logger = new RecordingLogger();
        var clock = new ManualTelemetryClock();
        await using var client = new AppTelemetryClient(
            httpClient,
            CreateOptions(),
            new FixedIdentityProvider(MachineId),
            clock,
            logger);

        await client.StartAsync();
        var start = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        _ = await handler.ReadNextAsync();
        var retryDelay = await clock.AdvanceOneDelayAsync();
        var resume = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        _ = await handler.ReadNextAsync();
        await client.StopAsync();
        _ = await handler.ReadNextAsync();

        var startBody = DistributionJson.Deserialize<TelemetryStartRequest>(start.Body);
        var resumeBody = DistributionJson.Deserialize<TelemetryResumeRequest>(resume.Body);
        Assert.Equal(TimeSpan.FromSeconds(2), retryDelay);
        Assert.NotEqual(startBody.SessionId, resumeBody.SessionId);
        Assert.Equal(SessionTokenA, resume.AuthorizationParameter);
        Assert.Contains(
            logger.Events,
            entry => entry.EventName == "telemetry.session_resumed");
        Assert.All(
            logger.Events,
            entry =>
            {
                Assert.DoesNotContain(MachineId, entry.Rendered, StringComparison.Ordinal);
                Assert.DoesNotContain(startBody.SessionId, entry.Rendered, StringComparison.Ordinal);
                Assert.DoesNotContain(resumeBody.SessionId, entry.Rendered, StringComparison.Ordinal);
                Assert.DoesNotContain(SessionTokenA, entry.Rendered, StringComparison.Ordinal);
                Assert.DoesNotContain(SessionTokenB, entry.Rendered, StringComparison.Ordinal);
                Assert.DoesNotContain("sensitive failure", entry.Rendered, StringComparison.Ordinal);
                Assert.DoesNotContain("127.0.0.1", entry.Rendered, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task BusyResumeBacksOffAndReusesCandidateSession()
    {
        using var handler = new ScriptedTelemetryHandler(
            SessionResponse(SessionTokenA, startupCount: 6),
            ErrorResponse(HttpStatusCode.Conflict, "session_ended"),
            ErrorResponse(
                HttpStatusCode.ServiceUnavailable,
                "server_busy",
                TimeSpan.FromSeconds(3)),
            SessionResponse(SessionTokenB, startupCount: 6),
            NoContentResponse(),
            NoContentResponse());
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var clock = new ManualTelemetryClock();
        await using var client = new AppTelemetryClient(
            httpClient,
            CreateOptions(),
            new FixedIdentityProvider(MachineId),
            clock);

        await client.StartAsync();
        _ = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        _ = await handler.ReadNextAsync();
        var firstResume = await handler.ReadNextAsync();
        var retryDelay = await clock.AdvanceOneDelayAsync();
        var retriedResume = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        _ = await handler.ReadNextAsync();
        await client.StopAsync();
        _ = await handler.ReadNextAsync();

        var firstBody = DistributionJson.Deserialize<TelemetryResumeRequest>(firstResume.Body);
        var retriedBody =
            DistributionJson.Deserialize<TelemetryResumeRequest>(retriedResume.Body);
        Assert.Equal(TimeSpan.FromSeconds(3), retryDelay);
        Assert.Equal(firstBody, retriedBody);
        Assert.Equal(SessionTokenA, firstResume.AuthorizationParameter);
        Assert.Equal(SessionTokenA, retriedResume.AuthorizationParameter);
    }

    [Fact]
    public async Task ExpiredPreviousSegmentFallsBackToNewLaunch()
    {
        using var handler = new ScriptedTelemetryHandler(
            SessionResponse(SessionTokenA, startupCount: 4),
            ErrorResponse(HttpStatusCode.Conflict, "session_ended"),
            ErrorResponse(HttpStatusCode.NotFound, "session_not_found"),
            SessionResponse(SessionTokenC, startupCount: 5),
            NoContentResponse(),
            NoContentResponse());
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var clock = new ManualTelemetryClock();
        await using var client = new AppTelemetryClient(
            httpClient,
            CreateOptions(),
            new FixedIdentityProvider(MachineId),
            clock);

        await client.StartAsync();
        var firstStart = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        _ = await handler.ReadNextAsync();
        var resume = await handler.ReadNextAsync();
        var replacementStart = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        var replacementHeartbeat = await handler.ReadNextAsync();
        await client.StopAsync();
        var end = await handler.ReadNextAsync();

        var firstStartBody = DistributionJson.Deserialize<TelemetryStartRequest>(firstStart.Body);
        var resumeBody = DistributionJson.Deserialize<TelemetryResumeRequest>(resume.Body);
        var replacementBody =
            DistributionJson.Deserialize<TelemetryStartRequest>(replacementStart.Body);
        Assert.Equal(TelemetryEndpoints.ResumePath, resume.Path);
        Assert.Equal(TelemetryEndpoints.StartPath, replacementStart.Path);
        Assert.Null(replacementStart.AuthorizationParameter);
        Assert.NotEqual(firstStartBody.SessionId, replacementBody.SessionId);
        Assert.NotEqual(resumeBody.SessionId, replacementBody.SessionId);
        Assert.Equal(SessionTokenC, replacementHeartbeat.AuthorizationParameter);
        Assert.Equal(SessionTokenC, end.AuthorizationParameter);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(507)]
    public async Task TransientServerStatusBacksOffBeforeRetryingHeartbeat(int statusCode)
    {
        using var handler = new ScriptedTelemetryHandler(
            SessionResponse(SessionTokenA, startupCount: 9),
            ErrorResponse(
                (HttpStatusCode)statusCode,
                "transient_failure",
                TimeSpan.FromSeconds(3)),
            NoContentResponse(),
            NoContentResponse());
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var clock = new ManualTelemetryClock();
        await using var client = new AppTelemetryClient(
            httpClient,
            CreateOptions(),
            new FixedIdentityProvider(MachineId),
            clock);

        await client.StartAsync();
        _ = await handler.ReadNextAsync();
        await clock.AdvanceOneDelayAsync();
        _ = await handler.ReadNextAsync();
        var retryDelay = await clock.AdvanceOneDelayAsync();
        var retriedHeartbeat = await handler.ReadNextAsync();
        await client.StopAsync();
        var end = await handler.ReadNextAsync();

        Assert.Equal(TimeSpan.FromSeconds(3), retryDelay);
        Assert.Equal(TelemetryEndpoints.HeartbeatPath, retriedHeartbeat.Path);
        Assert.Equal(SessionTokenA, retriedHeartbeat.AuthorizationParameter);
        Assert.Equal(SessionTokenA, end.AuthorizationParameter);
    }

    [Fact]
    public async Task DisposeSendsNormalEndAfterCancellingBackgroundHeartbeat()
    {
        using var handler = new RecordingTelemetryHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var clock = new ManualTelemetryClock();
        var client = new AppTelemetryClient(
            httpClient,
            CreateOptions(),
            new FixedIdentityProvider(MachineId),
            clock);

        await client.StartAsync();
        var start = await handler.ReadNextAsync();
        await clock.WaitForDelayScheduledAsync();

        client.Dispose();
        var end = await handler.ReadNextAsync();

        Assert.Equal(TelemetryEndpoints.StartPath, start.Path);
        Assert.Equal(TelemetryEndpoints.EndPath, end.Path);
    }

    [Fact]
    public async Task NetworkFailureNeverBlocksStartupAndStopRemainsBounded()
    {
        using var handler = new BlockingTelemetryHandler();
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var logger = new RecordingLogger();
        var clock = new ManualTelemetryClock();
        await using var client = new AppTelemetryClient(
            httpClient,
            CreateOptions(requestTimeout: TimeSpan.FromMilliseconds(50)),
            new FixedIdentityProvider(MachineId),
            clock,
            logger: logger);

        var startup = client.StartAsync();
        var completed = await Task.WhenAny(startup, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.Same(startup, completed);
        await startup;
        await handler.WaitForCallAsync();
        await clock.WaitForDelayScheduledAsync();

        var stopwatch = Stopwatch.StartNew();
        await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.True(handler.CallCount >= 1);
        Assert.Contains(
            logger.Events,
            entry => entry.EventName == "telemetry.request_failed");
        Assert.All(
            logger.Events,
            entry =>
            {
                Assert.DoesNotContain(MachineId, entry.Rendered, StringComparison.Ordinal);
                Assert.DoesNotContain("127.0.0.1", entry.Rendered, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void OptionsRequireSecureSlashTerminatedBaseAddress()
    {
        var defaults = new TelemetryClientOptions(new Uri("https://telemetry.example.test/"));
        _ = new TelemetryClientOptions(new Uri("http://127.0.0.1:8123/"));
        _ = new TelemetryClientOptions(
            new Uri("http://43.159.148.243/"),
            allowInsecureRemoteHttp: true);

        Assert.Throws<ArgumentException>(() =>
            new TelemetryClientOptions(new Uri("http://telemetry.example.test/")));
        Assert.Throws<ArgumentException>(() =>
            new TelemetryClientOptions(new Uri("https://telemetry.example.test/api")));
        Assert.Throws<ArgumentException>(() =>
            new TelemetryClientOptions(new Uri("https://telemetry.example.test/?token=secret")));
        Assert.Equal(TimeSpan.FromMinutes(2), defaults.HeartbeatInterval);
    }

    private static Func<RecordedRequest, HttpResponseMessage> SessionResponse(
        string sessionToken,
        int startupCount) =>
        _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                DistributionJson.Serialize(new TelemetryStartResponse(
                    startupCount,
                    new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero),
                    sessionToken)),
                Encoding.UTF8,
                "application/json"),
        };

    private static Func<RecordedRequest, HttpResponseMessage> ErrorResponse(
        HttpStatusCode statusCode,
        string code,
        TimeSpan? retryAfter = null) =>
        _ =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    DistributionJson.Serialize(new TelemetryErrorResponse(code)),
                    Encoding.UTF8,
                    "application/json"),
            };
            if (retryAfter is not null)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(
                    retryAfter.Value);
            }

            return response;
        };

    private static Func<RecordedRequest, HttpResponseMessage> NoContentResponse() =>
        _ => new HttpResponseMessage(HttpStatusCode.NoContent);

    private static TelemetryClientOptions CreateOptions(TimeSpan? requestTimeout = null) =>
        new(
            new Uri("http://127.0.0.1:8123/"),
            heartbeatInterval: TimeSpan.FromMinutes(1),
            requestTimeout: requestTimeout ?? TimeSpan.FromSeconds(1),
            initialRetryDelay: TimeSpan.FromSeconds(2),
            maximumRetryDelay: TimeSpan.FromSeconds(8));

    private sealed class FixedIdentityProvider(string machineId) : IMachineIdentityProvider
    {
        public ValueTask<string> GetMachineIdAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(machineId);
    }

    private sealed class ManualTelemetryClock : ITelemetryClock
    {
        private readonly Channel<DelayRequest> _delays = Channel.CreateUnbounded<DelayRequest>();
        private readonly object _gate = new();
        private DateTimeOffset _utcNow = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_gate)
                {
                    return _utcNow;
                }
            }
        }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new DelayRequest(delay, completion);
            await _delays.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                completion);
            await completion.Task.ConfigureAwait(false);
        }

        public async Task<TimeSpan> AdvanceOneDelayAsync()
        {
            using var timeout = new CancellationTokenSource(TestWaitTimeout);
            var request = await _delays.Reader
                .ReadAsync(timeout.Token)
                .ConfigureAwait(false);
            lock (_gate)
            {
                _utcNow += request.Delay;
            }

            request.Completion.TrySetResult();
            return request.Delay;
        }

        public async Task WaitForDelayScheduledAsync()
        {
            using var timeout = new CancellationTokenSource(TestWaitTimeout);
            _ = await _delays.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }

        private sealed record DelayRequest(TimeSpan Delay, TaskCompletionSource Completion);
    }

    private sealed class RecordingTelemetryHandler : HttpMessageHandler
    {
        private readonly Channel<RecordedRequest> _requests =
            Channel.CreateUnbounded<RecordedRequest>();
        private int _startupCount;

        public async Task<RecordedRequest> ReadNextAsync()
        {
            using var timeout = new CancellationTokenSource(TestWaitTimeout);
            return await _requests.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            await _requests.Writer.WriteAsync(
                    new RecordedRequest(
                        request.Method,
                        request.RequestUri?.AbsolutePath ?? string.Empty,
                        request.Content?.Headers.ContentType?.MediaType,
                        body,
                        request.Headers.Authorization?.Scheme,
                        request.Headers.Authorization?.Parameter),
                    cancellationToken)
                .ConfigureAwait(false);

            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.RequestUri?.AbsolutePath == TelemetryEndpoints.StartPath)
            {
                var startupCount = Interlocked.Increment(ref _startupCount);
                response.StatusCode = HttpStatusCode.OK;
                response.Content = new StringContent(
                    DistributionJson.Serialize(new TelemetryStartResponse(
                        startupCount,
                        new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero),
                        SessionTokenA)),
                    Encoding.UTF8,
                    "application/json");
            }

            return response;
        }
    }

    private sealed class ScriptedTelemetryHandler : HttpMessageHandler
    {
        private readonly Channel<RecordedRequest> _requests =
            Channel.CreateUnbounded<RecordedRequest>();
        private readonly ConcurrentQueue<Func<RecordedRequest, HttpResponseMessage>> _responses;

        public ScriptedTelemetryHandler(
            params Func<RecordedRequest, HttpResponseMessage>[] responses)
        {
            _responses = new ConcurrentQueue<Func<RecordedRequest, HttpResponseMessage>>(
                responses);
        }

        public async Task<RecordedRequest> ReadNextAsync()
        {
            using var timeout = new CancellationTokenSource(TestWaitTimeout);
            return await _requests.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var recorded = new RecordedRequest(
                request.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Content?.Headers.ContentType?.MediaType,
                body,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter);
            await _requests.Writer.WriteAsync(recorded, cancellationToken).ConfigureAwait(false);
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No scripted telemetry response remained.");
            }

            return response(recorded);
        }
    }

    private sealed class BlockingTelemetryHandler : HttpMessageHandler
    {
        private readonly SemaphoreSlim _callSignal = new(0);
        private readonly CancellationTokenSource _disposeCancellation = new();
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task WaitForCallAsync()
        {
            using var timeout = new CancellationTokenSource(TestWaitTimeout);
            await _callSignal.WaitAsync(timeout.Token).ConfigureAwait(false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            _ = Interlocked.Increment(ref _callCount);
            _callSignal.Release();
            await Task.Delay(Timeout.InfiniteTimeSpan, _disposeCancellation.Token)
                .ConfigureAwait(false);
            throw new InvalidOperationException("The fixture request should only end on disposal.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposeCancellation.Cancel();
                _disposeCancellation.Dispose();
                _callSignal.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CountingReadStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly int _expectedBytesRead;
        private readonly TaskCompletionSource _expectedRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private long _bytesRead;

        public CountingReadStream(byte[] content, int expectedBytesRead)
        {
            _inner = new MemoryStream(content, writable: false);
            _expectedBytesRead = expectedBytesRead;
        }

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public Task WaitForExpectedReadAsync() =>
            _expectedRead.Task.WaitAsync(TestWaitTimeout);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _inner.Read(buffer, offset, count);
            RecordRead(bytesRead);
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            var bytesRead = _inner.Read(buffer);
            RecordRead(bytesRead);
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var bytesRead = await _inner
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            RecordRead(bytesRead);
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void RecordRead(int count)
        {
            if (count <= 0)
            {
                return;
            }

            var total = Interlocked.Add(ref _bytesRead, count);
            if (total >= _expectedBytesRead)
            {
                _expectedRead.TrySetResult();
            }
        }
    }

    private sealed class RecordingLogger : IAppLogger
    {
        private readonly ConcurrentQueue<LogEntry> _events = new();

        public IReadOnlyCollection<LogEntry> Events => _events.ToArray();

        public void Log(
            AppLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? properties = null,
            Exception? exception = null)
        {
            var propertyText = properties is null
                ? string.Empty
                : string.Join('|', properties.Select(pair => $"{pair.Key}={pair.Value}"));
            _events.Enqueue(new LogEntry(eventName, $"{message}|{propertyText}"));
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string? ContentType,
        string Body,
        string? AuthorizationScheme,
        string? AuthorizationParameter);

    private sealed record LogEntry(string EventName, string Rendered);
}
