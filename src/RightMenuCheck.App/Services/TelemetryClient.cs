using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.App.Services;

public static class TelemetryEndpoints
{
    public const string StartPath = "/v1/telemetry/start";
    public const string ResumePath = "/v1/telemetry/resume";
    public const string HeartbeatPath = "/v1/telemetry/heartbeat";
    public const string EndPath = "/v1/telemetry/end";
}

public sealed record TelemetryClientOptions
{
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultInitialRetryDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultMaximumRetryDelay = TimeSpan.FromMinutes(5);

    public TelemetryClientOptions(
        Uri baseAddress,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maximumRetryDelay = null,
        bool allowInsecureRemoteHttp = false)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ValidateBaseAddress(baseAddress, allowInsecureRemoteHttp);

        BaseAddress = baseAddress;
        HeartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        RequestTimeout = requestTimeout ?? DefaultRequestTimeout;
        InitialRetryDelay = initialRetryDelay ?? DefaultInitialRetryDelay;
        MaximumRetryDelay = maximumRetryDelay ?? DefaultMaximumRetryDelay;
        AllowsInsecureRemoteHttp = allowInsecureRemoteHttp;
        if (HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatInterval),
                "The heartbeat interval must be positive.");
        }

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The telemetry request timeout must be finite and positive.");
        }


        if (InitialRetryDelay <= TimeSpan.Zero || MaximumRetryDelay < InitialRetryDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialRetryDelay),
                "Telemetry retry delays must be positive and ordered.");
        }
    }

    public Uri BaseAddress { get; }

    public TimeSpan HeartbeatInterval { get; }

    public TimeSpan RequestTimeout { get; }

    public TimeSpan InitialRetryDelay { get; }

    public TimeSpan MaximumRetryDelay { get; }

    public bool AllowsInsecureRemoteHttp { get; }

    internal Uri Resolve(string absolutePath) =>
        new(BaseAddress, absolutePath.TrimStart('/'));

    private static void ValidateBaseAddress(Uri baseAddress, bool allowInsecureRemoteHttp)
    {
        var isSecure = baseAddress.Scheme.Equals(
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
        var isLoopbackTestAddress = baseAddress.Scheme.Equals(
                                        Uri.UriSchemeHttp,
                                        StringComparison.OrdinalIgnoreCase) &&
                                    baseAddress.IsLoopback;
        var isSignedInsecureEndpoint = allowInsecureRemoteHttp &&
                                       baseAddress.Scheme.Equals(
                                           Uri.UriSchemeHttp,
                                           StringComparison.OrdinalIgnoreCase);
        if (!baseAddress.IsAbsoluteUri ||
            (!isSecure && !isLoopbackTestAddress && !isSignedInsecureEndpoint))
        {
            throw new ArgumentException(
                "The telemetry base address must use HTTPS, except for loopback tests.",
                nameof(baseAddress));
        }

        if (!baseAddress.AbsoluteUri.EndsWith('/') ||
            !string.IsNullOrEmpty(baseAddress.Query) ||
            !string.IsNullOrEmpty(baseAddress.Fragment) ||
            !string.IsNullOrEmpty(baseAddress.UserInfo))
        {
            throw new ArgumentException(
                "The telemetry base address must end with '/' and cannot contain credentials, " +
                "a query, or a fragment.",
                nameof(baseAddress));
        }
    }
}

public interface ITelemetryClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemTelemetryClock : ITelemetryClock
{
    private SystemTelemetryClock()
    {
    }

    public static SystemTelemetryClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public interface IAppTelemetryClient : IDisposable, IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class AppTelemetryClient : IAppTelemetryClient
{
    private const int MaximumErrorResponseBytes = 4096;

    private readonly object _stateGate = new();
    private readonly HttpClient _httpClient;
    private readonly IMachineIdentityProvider _identityProvider;
    private readonly IAppLogger _logger;
    private readonly TelemetryClientOptions _options;
    private readonly ITelemetryClock _clock;
    private readonly bool _ownsHttpClient;

    private CancellationTokenSource? _sessionCancellation;
    private Task? _sessionTask;
    private Task? _stopTask;
    private SessionCredentials? _activeSession;
    private int _consecutiveHeartbeatFailures;
    private int _disposeStarted;
    private LifecycleState _state;

    public AppTelemetryClient(
        TelemetryClientOptions options,
        IMachineIdentityProvider identityProvider,
        IAppLogger? logger = null)
        : this(
            CreateHttpClient(),
            options,
            identityProvider,
            SystemTelemetryClock.Instance,
            logger,
            ownsHttpClient: true)
    {
    }

    public AppTelemetryClient(
        HttpClient httpClient,
        TelemetryClientOptions options,
        IMachineIdentityProvider identityProvider,
        ITelemetryClock? clock = null,
        IAppLogger? logger = null)
        : this(
            httpClient,
            options,
            identityProvider,
            clock ?? SystemTelemetryClock.Instance,
            logger,
            ownsHttpClient: false)
    {
    }

    private AppTelemetryClient(
        HttpClient httpClient,
        TelemetryClientOptions options,
        IMachineIdentityProvider identityProvider,
        ITelemetryClock clock,
        IAppLogger? logger,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identityProvider);
        ArgumentNullException.ThrowIfNull(clock);

        _httpClient = httpClient;
        _options = options;
        _identityProvider = identityProvider;
        _clock = clock;
        _logger = logger ?? NullAppLogger.Instance;
        _ownsHttpClient = ownsHttpClient;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0,
                this);
            if (_state == LifecycleState.Running)
            {
                return Task.CompletedTask;
            }

            if (_state == LifecycleState.Stopped)
            {
                throw new InvalidOperationException(
                    "A stopped telemetry client cannot be started again.");
            }

            _state = LifecycleState.Running;
            _sessionCancellation = new CancellationTokenSource();
            _sessionTask = Task.Run(
                () => RunSessionAsync(_sessionCancellation.Token),
                CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (_stateGate)
        {
            if (_stopTask is null)
            {
                _state = LifecycleState.Stopped;
                _stopTask = Task.Run(
                    () => StopCoreAsync(_sessionTask, _sessionCancellation),
                    CancellationToken.None);
            }

            stopTask = _stopTask;
        }

        return stopTask.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var machineId = await _identityProvider
                .GetMachineIdAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!TelemetryIdentityValidator.IsValidMachineId(machineId))
            {
                LogLifecycleFailure(
                    "identity",
                    "invalid_identity",
                    exception: null);
                return;
            }

            var activeSession = await StartLaunchAsync(machineId, cancellationToken)
                .ConfigureAwait(false);
            if (activeSession is null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _activeSession = activeSession;
            var heartbeatDelay = _options.HeartbeatInterval;
            var heartbeatRetryAttempt = 0;

            while (true)
            {
                await _clock
                    .DelayAsync(heartbeatDelay, cancellationToken)
                    .ConfigureAwait(false);
                var heartbeatResult = await PostAsync(
                        TelemetryEndpoints.HeartbeatPath,
                        new TelemetryHeartbeatRequest(
                            activeSession.MachineId,
                            activeSession.SessionId),
                        activeSession.SessionToken,
                        readResponseBody: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (heartbeatResult.Succeeded)
                {
                    HandleHeartbeatSuccess();
                    heartbeatDelay = _options.HeartbeatInterval;
                    heartbeatRetryAttempt = 0;
                    continue;
                }

                HandleHeartbeatFailure(heartbeatResult);
                if (IsBackoffStatus(heartbeatResult))
                {
                    heartbeatRetryAttempt++;
                    heartbeatDelay = GetRetryDelay(
                        heartbeatResult,
                        heartbeatRetryAttempt);
                    continue;
                }

                if (!ShouldResume(heartbeatResult))
                {
                    return;
                }

                var resumedSession = await ResumeSessionAsync(
                        activeSession,
                        heartbeatResult,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (resumedSession is null || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                activeSession = resumedSession;
                _activeSession = resumedSession;
                heartbeatDelay = _options.HeartbeatInterval;
                heartbeatRetryAttempt = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogLifecycleFailure(
                "session",
                "unexpected_failure",
                exception);
        }
    }

    private async Task StopCoreAsync(
        Task? sessionTask,
        CancellationTokenSource? sessionCancellation)
    {
        sessionCancellation?.Cancel();
        try
        {
            if (sessionTask is not null)
            {
                await sessionTask.ConfigureAwait(false);
            }

            var activeSession = _activeSession;
            if (activeSession is null)
            {
                return;
            }

            var endResult = await PostAsync(
                    TelemetryEndpoints.EndPath,
                    new TelemetryEndRequest(
                        activeSession.MachineId,
                        activeSession.SessionId),
                    activeSession.SessionToken,
                    readResponseBody: false,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (endResult.Succeeded)
            {
                _logger.Log(
                    AppLogLevel.Information,
                    "telemetry.session_ended",
                    "The telemetry session ended normally.");
            }
            else
            {
                LogRequestFailure("end", endResult, consecutiveFailures: null);
            }
        }
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogLifecycleFailure("end", "unexpected_failure", exception);
        }
        finally
        {
            sessionCancellation?.Dispose();
        }
    }

    private async Task<SessionCredentials?> StartLaunchAsync(
        string machineId,
        CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var retryAttempt = 0;
        while (true)
        {
            var result = await PostAsync(
                    TelemetryEndpoints.StartPath,
                    new TelemetryStartRequest(machineId, sessionId),
                    sessionToken: null,
                    readResponseBody: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (result.Succeeded)
            {
                if (!TryReadSessionCredentials(
                        result,
                        machineId,
                        sessionId,
                        expectedStartupCount: null,
                        "start",
                        out var credentials,
                        out var response))
                {
                    return null;
                }

                LogSessionAccepted(
                    "telemetry.session_started",
                    "The telemetry session started.",
                    response);
                return credentials;
            }

            LogRequestFailure("start", result, consecutiveFailures: null);
            if (!IsRetryable(result))
            {
                return null;
            }

            retryAttempt++;
            await DelayForRetryAsync(result, retryAttempt, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<SessionCredentials?> ResumeSessionAsync(
        SessionCredentials previous,
        TelemetryPostResult interruption,
        CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var retryAttempt = 0;
        if (IsRetryable(interruption))
        {
            retryAttempt++;
            await DelayForRetryAsync(interruption, retryAttempt, cancellationToken)
                .ConfigureAwait(false);
        }

        while (true)
        {
            var result = await PostAsync(
                    TelemetryEndpoints.ResumePath,
                    new TelemetryResumeRequest(
                        previous.MachineId,
                        previous.SessionId,
                        sessionId),
                    previous.SessionToken,
                    readResponseBody: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (result.Succeeded)
            {
                if (!TryReadSessionCredentials(
                        result,
                        previous.MachineId,
                        sessionId,
                        previous.StartupCount,
                        "resume",
                        out var credentials,
                        out var response))
                {
                    return null;
                }

                var previousFailures = Interlocked.Exchange(
                    ref _consecutiveHeartbeatFailures,
                    0);
                LogSessionAccepted(
                    "telemetry.session_resumed",
                    "The telemetry session resumed with a new timing segment.",
                    response,
                    previousFailures);
                return credentials;
            }

            LogRequestFailure(
                "resume",
                result,
                Volatile.Read(ref _consecutiveHeartbeatFailures));
            if (IsExpiredSession(result))
            {
                _logger.Log(
                    AppLogLevel.Information,
                    "telemetry.session_expired",
                    "The previous telemetry segment expired; a new launch is starting.");
                return await StartLaunchAsync(previous.MachineId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!IsRetryable(result))
            {
                return null;
            }

            retryAttempt++;
            await DelayForRetryAsync(result, retryAttempt, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private bool TryReadSessionCredentials(
        TelemetryPostResult result,
        string machineId,
        string sessionId,
        int? expectedStartupCount,
        string operation,
        out SessionCredentials credentials,
        out TelemetryStartResponse response)
    {
        try
        {
            response = DistributionJson.Deserialize<TelemetryStartResponse>(
                result.ResponseBody ?? string.Empty);
            if (response.StartupCount < 1 ||
                (expectedStartupCount is not null &&
                 response.StartupCount != expectedStartupCount.Value) ||
                !TelemetryIdentityValidator.IsValidSessionToken(response.SessionToken))
            {
                throw new InvalidDataException("The telemetry session response was invalid.");
            }

            credentials = new SessionCredentials(
                machineId,
                sessionId,
                response.SessionToken,
                response.StartupCount);
            return true;
        }
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            credentials = null!;
            response = null!;
            LogLifecycleFailure(operation, "invalid_response", exception);
            return false;
        }
    }

    private void HandleHeartbeatSuccess()
    {
        var previousFailures = Interlocked.Exchange(
            ref _consecutiveHeartbeatFailures,
            0);
        if (previousFailures > 0)
        {
            _logger.Log(
                AppLogLevel.Information,
                "telemetry.heartbeat_recovered",
                "Telemetry heartbeat delivery recovered.",
                new Dictionary<string, object?>
                {
                    ["previousConsecutiveFailures"] = previousFailures,
                });
        }
    }

    private void HandleHeartbeatFailure(TelemetryPostResult result)
    {
        var failures = Interlocked.Increment(ref _consecutiveHeartbeatFailures);
        if (failures == 1 || failures % 10 == 0)
        {
            LogRequestFailure("heartbeat", result, failures);
        }
    }

    private void LogSessionAccepted(
        string eventName,
        string message,
        TelemetryStartResponse response,
        int? previousFailures = null)
    {
        _logger.Log(
            AppLogLevel.Information,
            eventName,
            message,
            new Dictionary<string, object?>
            {
                ["startupCount"] = response.StartupCount,
                ["serverStartedAtUtc"] = response.StartedAtUtc,
                ["clientObservedAtUtc"] = _clock.UtcNow,
                ["previousConsecutiveFailures"] = previousFailures,
            });
    }

    private async Task DelayForRetryAsync(
        TelemetryPostResult result,
        int retryAttempt,
        CancellationToken cancellationToken)
    {
        var delay = GetRetryDelay(result, retryAttempt);
        await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    private TimeSpan GetRetryDelay(TelemetryPostResult result, int retryAttempt) =>
        result.RetryAfter is { } serverDelay && serverDelay > TimeSpan.Zero
            ? Min(serverDelay, _options.MaximumRetryDelay)
            : CalculateRetryDelay(retryAttempt);

    private TimeSpan CalculateRetryDelay(int retryAttempt)
    {
        var ticks = _options.InitialRetryDelay.Ticks;
        for (var attempt = 1;
             attempt < retryAttempt && ticks < _options.MaximumRetryDelay.Ticks;
             attempt++)
        {
            ticks = Math.Min(
                _options.MaximumRetryDelay.Ticks,
                ticks > _options.MaximumRetryDelay.Ticks / 2
                    ? _options.MaximumRetryDelay.Ticks
                    : ticks * 2);
        }

        return TimeSpan.FromTicks(ticks);
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) =>
        first <= second ? first : second;

    private static bool ShouldResume(TelemetryPostResult result) =>
        IsConnectionInterruption(result) ||
        IsExpiredSession(result) ||
        result.StatusCode == (int)HttpStatusCode.Conflict &&
        string.Equals(result.ErrorCode, "session_ended", StringComparison.Ordinal);

    private static bool IsExpiredSession(TelemetryPostResult result) =>
        result.StatusCode == (int)HttpStatusCode.NotFound &&
        string.Equals(result.ErrorCode, "session_not_found", StringComparison.Ordinal);

    private static bool IsRetryable(TelemetryPostResult result) =>
        IsBackoffStatus(result) || IsConnectionInterruption(result);

    private static bool IsBackoffStatus(TelemetryPostResult result) =>
        result.StatusCode is 429 or 503 or 504 or 507;

    private static bool IsConnectionInterruption(TelemetryPostResult result) =>
        result.StatusCode is null &&
        result.FailureReason is "timeout" or "transport";

    private async Task<TelemetryPostResult> PostAsync<TRequest>(
        string endpointPath,
        TRequest body,
        string? sessionToken,
        bool readResponseBody,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            _options.Resolve(endpointPath))
        {
            Content = new StringContent(
                DistributionJson.Serialize(body),
                Encoding.UTF8,
                "application/json"),
        };
        if (sessionToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                sessionToken);
        }

        var stopwatch = Stopwatch.StartNew();
        Task<HttpResponseMessage>? sendTask = null;
        try
        {
            sendTask = _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            using var response = await sendTask
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorCode = await ReadKnownErrorCodeAsync(response, timeout.Token)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                return TelemetryPostResult.HttpFailure(
                    (int)response.StatusCode,
                    errorCode,
                    ReadRetryAfter(response),
                    stopwatch.Elapsed);
            }

            var responseBody = readResponseBody
                ? await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)
                : null;
            stopwatch.Stop();
            return TelemetryPostResult.Success(responseBody, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            ObserveLateResponse(sendTask);
            return TelemetryPostResult.Failure(
                "timeout",
                exceptionType: nameof(OperationCanceledException),
                exceptionHResult: null,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            ObserveLateResponse(sendTask);
            return TelemetryPostResult.Failure(
                "cancelled",
                exceptionType: nameof(OperationCanceledException),
                exceptionHResult: null,
                stopwatch.Elapsed);
        }
#pragma warning disable CA1031
        catch (Exception exception)
#pragma warning restore CA1031
        {
            stopwatch.Stop();
            ObserveLateResponse(sendTask);
            return TelemetryPostResult.Failure(
                "transport",
                exception.GetType().Name,
                exception.HResult,
                stopwatch.Elapsed);
        }
    }

    private static async Task<string?> ReadKnownErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength is > MaximumErrorResponseBytes)
            {
                return null;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var buffer = new byte[MaximumErrorResponseBytes + 1];
            var bytesRead = 0;
            while (bytesRead < buffer.Length)
            {
                var count = await stream
                    .ReadAsync(buffer.AsMemory(bytesRead), cancellationToken)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                bytesRead += count;
            }

            if (bytesRead > MaximumErrorResponseBytes)
            {
                return null;
            }

            var body = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var code = DistributionJson.Deserialize<TelemetryErrorResponse>(body).Code;
            return code is "session_ended" or "session_not_found" ? code : null;
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - _clock.UtcNow;
            return delay > TimeSpan.Zero ? delay : null;
        }

        return null;
    }

    private static void ObserveLateResponse(Task<HttpResponseMessage>? sendTask)
    {
        if (sendTask is null)
        {
            return;
        }

        _ = sendTask.ContinueWith(
            static completedTask =>
            {
                if (completedTask.Status == TaskStatus.RanToCompletion)
                {
                    completedTask.Result.Dispose();
                }
                else
                {
                    _ = completedTask.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void LogRequestFailure(
        string operation,
        TelemetryPostResult result,
        int? consecutiveFailures)
    {
        _logger.Log(
            AppLogLevel.Warning,
            "telemetry.request_failed",
            "A telemetry request did not complete successfully.",
            new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["reason"] = result.FailureReason,
                ["statusCode"] = result.StatusCode,
                ["exceptionType"] = result.ExceptionType,
                ["exceptionHResult"] = result.ExceptionHResult,
                ["durationMilliseconds"] = result.Duration.TotalMilliseconds,
                ["consecutiveFailures"] = consecutiveFailures,
            });
    }

    private void LogLifecycleFailure(
        string operation,
        string reason,
        Exception? exception)
    {
        _logger.Log(
            AppLogLevel.Warning,
            "telemetry.lifecycle_failed",
            "The telemetry lifecycle operation did not complete successfully.",
            new Dictionary<string, object?>
            {
                ["operation"] = operation,
                ["reason"] = reason,
                ["exceptionType"] = exception?.GetType().Name,
                ["exceptionHResult"] = exception?.HResult,
            });
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private enum LifecycleState
    {
        Created,
        Running,
        Stopped,
    }

    private sealed record SessionCredentials(
        string MachineId,
        string SessionId,
        string SessionToken,
        int StartupCount);

    private sealed record TelemetryPostResult(
        bool Succeeded,
        string? ResponseBody,
        int? StatusCode,
        string? ErrorCode,
        TimeSpan? RetryAfter,
        string? FailureReason,
        string? ExceptionType,
        int? ExceptionHResult,
        TimeSpan Duration)
    {
        public static TelemetryPostResult Success(string? responseBody, TimeSpan duration) =>
            new(
                Succeeded: true,
                responseBody,
                StatusCode: null,
                ErrorCode: null,
                RetryAfter: null,
                FailureReason: null,
                ExceptionType: null,
                ExceptionHResult: null,
                duration);

        public static TelemetryPostResult HttpFailure(
            int statusCode,
            string? errorCode,
            TimeSpan? retryAfter,
            TimeSpan duration) =>
            new(
                Succeeded: false,
                ResponseBody: null,
                statusCode,
                errorCode,
                retryAfter,
                FailureReason: "http_status",
                ExceptionType: null,
                ExceptionHResult: null,
                duration);

        public static TelemetryPostResult Failure(
            string reason,
            string exceptionType,
            int? exceptionHResult,
            TimeSpan duration) =>
            new(
                Succeeded: false,
                ResponseBody: null,
                StatusCode: null,
                ErrorCode: null,
                RetryAfter: null,
                reason,
                exceptionType,
                exceptionHResult,
                duration);
    }
}
