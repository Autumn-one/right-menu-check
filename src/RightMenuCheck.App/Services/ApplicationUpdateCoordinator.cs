using System.IO;
using System.Net.Http;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.App.Services;

public sealed record ApplicationUpdatePreparation(
    ApplicationUpdateCheck Check,
    PreparedApplicationUpdate? PreparedUpdate);

public interface IUpdateMonitorClock
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemUpdateMonitorClock : IUpdateMonitorClock
{
    private SystemUpdateMonitorClock()
    {
    }

    public static SystemUpdateMonitorClock Instance { get; } = new();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class ApplicationUpdateCoordinator : IDisposable
{
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly TimeSpan _checkInterval;
    private readonly IUpdateMonitorClock _clock;
    private readonly object _lifecycleLock = new();
    private readonly IAppLogger _logger;
    private readonly IApplicationUpdateService _updateService;
    private int _activeChecks;
    private bool _cleanupStarted;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private int _disposeStarted;

    public ApplicationUpdateCoordinator(
        IApplicationUpdateService updateService,
        IAppLogger logger,
        IUpdateMonitorClock? clock = null,
        TimeSpan? checkInterval = null)
    {
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? SystemUpdateMonitorClock.Instance;
        _checkInterval = checkInterval ?? DefaultCheckInterval;
        if (_checkInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkInterval),
                "The update check interval must be positive.");
        }
    }

    public async Task<ApplicationUpdatePreparation> CheckAndPrepareAsync(
        IProgress<double>? downloadProgress,
        CancellationToken cancellationToken)
    {
        EnterCheck();
        var gateEntered = false;
        try
        {
            await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            var check = await _updateService.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (check is not
                {
                    State: ApplicationUpdateState.Required,
                    Manifest: { } manifest,
                })
            {
                return new ApplicationUpdatePreparation(check, PreparedUpdate: null);
            }

            var prepared = await _updateService
                .PrepareAsync(manifest, downloadProgress, cancellationToken)
                .ConfigureAwait(false);
            _logger.Log(
                AppLogLevel.Information,
                "update.background_download_completed",
                "A required update package was downloaded and verified.",
                new Dictionary<string, object?>
                {
                    ["targetVersion"] = prepared.TargetVersion.ToString(),
                });
            return new ApplicationUpdatePreparation(check, prepared);
        }
        finally
        {
            if (gateEntered)
            {
                _checkGate.Release();
            }

            ExitCheck();
        }
    }

    public void Start(
        Func<PreparedApplicationUpdate, CancellationToken, Task<bool>> promptAsync,
        CancellationToken applicationCancellation)
    {
        ArgumentNullException.ThrowIfNull(promptAsync);
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted != 0, this);
            if (_monitorTask is not null)
            {
                throw new InvalidOperationException("The update monitor is already running.");
            }

            _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                applicationCancellation);
            _monitorTask = Task.Run(
                () => RunMonitorAsync(promptAsync, _monitorCancellation.Token),
                CancellationToken.None);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        CancellationTokenSource? cancellation;
        Task? monitorTask;
        lock (_lifecycleLock)
        {
            cancellation = _monitorCancellation;
            monitorTask = _monitorTask;
        }

        cancellation?.Cancel();
        if (monitorTask is not null)
        {
            _ = monitorTask.ContinueWith(
                static (_, state) =>
                    ((ApplicationUpdateCoordinator)state!).TryCleanup(),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        TryCleanup();
        GC.SuppressFinalize(this);
    }

    private async Task RunMonitorAsync(
        Func<PreparedApplicationUpdate, CancellationToken, Task<bool>> promptAsync,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await _clock.DelayAsync(_checkInterval, cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await CheckAndPrepareAsync(
                        downloadProgress: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.PreparedUpdate is { } prepared &&
                    await promptAsync(prepared, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _logger.Log(
                    AppLogLevel.Warning,
                    "update.background_check_failed",
                    "The background update check did not complete.",
                    new Dictionary<string, object?>
                    {
                        ["errorType"] = exception.GetType().Name,
                    });
            }
        }
    }

    private void EnterCheck()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted != 0, this);
            _activeChecks++;
        }
    }

    private void ExitCheck()
    {
        lock (_lifecycleLock)
        {
            _activeChecks--;
        }

        TryCleanup();
    }

    private void TryCleanup()
    {
        CancellationTokenSource? cancellation = null;
        Task? monitorTask = null;
        lock (_lifecycleLock)
        {
            if (_cleanupStarted ||
                _disposeStarted == 0 ||
                _activeChecks != 0 ||
                _monitorTask is { IsCompleted: false })
            {
                return;
            }

            _cleanupStarted = true;
            cancellation = _monitorCancellation;
            monitorTask = _monitorTask;
        }

        if (monitorTask?.IsFaulted == true)
        {
            _ = monitorTask.Exception;
        }

        cancellation?.Dispose();
        _checkGate.Dispose();
    }

    private static bool IsRecoverable(Exception exception) => exception is
        HttpRequestException or
        IOException or
        InvalidDataException or
        InvalidOperationException or
        TimeoutException or
        UnauthorizedAccessException;
}
