using System.Security.Cryptography;
using System.Threading.Channels;
using RightMenuCheck.App.Services;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.IntegrationTests.Distribution;

public sealed class ApplicationUpdateCoordinatorTests
{
    [Fact]
    public async Task StartupCheckImmediatelyDownloadsRequiredPackage()
    {
        var fixture = CreateRequiredUpdate();
        var service = new FakeUpdateService(fixture.Check, fixture.Prepared);
        var clock = new ManualUpdateClock();
        using var coordinator = new ApplicationUpdateCoordinator(
            service,
            NullAppLogger.Instance,
            clock);

        var result = await coordinator.CheckAndPrepareAsync(
            downloadProgress: null,
            CancellationToken.None);

        Assert.Same(fixture.Prepared, result.PreparedUpdate);
        Assert.Equal(1, service.CheckCalls);
        Assert.Equal(1, service.PrepareCalls);
        Assert.Equal(0, clock.DelayCalls);
    }

    [Fact]
    public async Task BackgroundMonitorWaitsFiveMinutesAndPromptsAfterPreparation()
    {
        var fixture = CreateRequiredUpdate();
        var service = new FakeUpdateService(fixture.Check, fixture.Prepared);
        var clock = new ManualUpdateClock();
        using var coordinator = new ApplicationUpdateCoordinator(
            service,
            NullAppLogger.Instance,
            clock);
        var prompted = new TaskCompletionSource<PreparedApplicationUpdate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Start(
            (prepared, _) =>
            {
                prompted.TrySetResult(prepared);
                return Task.FromResult(true);
            },
            CancellationToken.None);

        var delay = await clock.AdvanceAsync();
        var update = await prompted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromMinutes(5), delay);
        Assert.Same(fixture.Prepared, update);
        Assert.Equal(1, service.CheckCalls);
        Assert.Equal(1, service.PrepareCalls);
    }

    [Fact]
    public async Task ConcurrentChecksAreSerialized()
    {
        var fixture = CreateRequiredUpdate();
        using var service = new BlockingUpdateService(fixture.Check);
        using var coordinator = new ApplicationUpdateCoordinator(
            service,
            NullAppLogger.Instance);

        var first = coordinator.CheckAndPrepareAsync(null, CancellationToken.None);
        await service.WaitForCallAsync(1);
        var second = coordinator.CheckAndPrepareAsync(null, CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(1, service.CheckCalls);
        Assert.Equal(1, service.MaximumConcurrentChecks);

        service.ReleaseOne();
        _ = await first;
        await service.WaitForCallAsync(2);
        service.ReleaseOne();
        _ = await second;

        Assert.Equal(2, service.CheckCalls);
        Assert.Equal(1, service.MaximumConcurrentChecks);
    }

    [Fact]
    public async Task DisposeCancelsMonitorWithoutWaitingForPromptCompletion()
    {
        var fixture = CreateRequiredUpdate();
        var service = new FakeUpdateService(fixture.Check, fixture.Prepared);
        var clock = new ManualUpdateClock();
        var coordinator = new ApplicationUpdateCoordinator(
            service,
            NullAppLogger.Instance,
            clock);
        var promptEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var promptCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrompt = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Start(
            async (_, token) =>
            {
                using var registration = token.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    promptCancellation);
                promptEntered.TrySetResult();
                await releasePrompt.Task;
                return false;
            },
            CancellationToken.None);

        _ = await clock.AdvanceAsync();
        await promptEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.Run(coordinator.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
        await promptCancellation.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releasePrompt.TrySetResult();
    }

    private static UpdateFixture CreateRequiredUpdate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = DateTimeOffset.UtcNow;
        var manifest = SignedUpdateManifest.Create(
            new UpdateManifestPayload(
                Sequence: 1,
                IssuedAtUtc: now,
                ExpiresAtUtc: now.AddDays(30),
                Version: "1.2.3",
                new UpdatePackage(
                    "update.zip",
                    1,
                    new string('A', 64),
                    "https://example.test/update.zip",
                    []),
                "Update",
                "https://example.test/release"),
            key.ExportPkcs8PrivateKeyPem());
        var check = new ApplicationUpdateCheck(
            ApplicationUpdateState.Required,
            manifest,
            new UpdateDecision(
                UpdateDecisionKind.Required,
                SemanticVersion.Parse("1.2.3"),
                "Required"),
            "Required");
        return new UpdateFixture(
            check,
            new PreparedApplicationUpdate(
                manifest,
                SemanticVersion.Parse("1.2.3"),
                "C:\\fixture\\update.zip"));
    }

    private sealed class FakeUpdateService(
        ApplicationUpdateCheck check,
        PreparedApplicationUpdate prepared) : IApplicationUpdateService
    {
        public int CheckCalls { get; private set; }

        public int PrepareCalls { get; private set; }

        public Task<ApplicationUpdateCheck> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCalls++;
            return Task.FromResult(check);
        }

        public Task<PreparedApplicationUpdate> PrepareAsync(
            SignedUpdateManifest manifest,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            _ = manifest;
            _ = progress;
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls++;
            return Task.FromResult(prepared);
        }

        public Task LaunchPreparedAsync(
            PreparedApplicationUpdate update,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The coordinator test never launches an updater.");
    }

    private sealed class BlockingUpdateService(ApplicationUpdateCheck check)
        : IApplicationUpdateService, IDisposable
    {
        private readonly SemaphoreSlim _entered = new(0);
        private readonly SemaphoreSlim _release = new(0);
        private int _activeChecks;
        private int _maximumConcurrentChecks;

        public int CheckCalls { get; private set; }

        public int MaximumConcurrentChecks => Volatile.Read(ref _maximumConcurrentChecks);

        public async Task<ApplicationUpdateCheck> CheckAsync(
            CancellationToken cancellationToken)
        {
            CheckCalls++;
            var active = Interlocked.Increment(ref _activeChecks);
            UpdateMaximum(active);
            _entered.Release();
            try
            {
                await _release.WaitAsync(cancellationToken);
                return check with { State = ApplicationUpdateState.Current };
            }
            finally
            {
                _ = Interlocked.Decrement(ref _activeChecks);
            }
        }

        public Task<PreparedApplicationUpdate> PrepareAsync(
            SignedUpdateManifest manifest,
            IProgress<double>? progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A current result is never prepared.");

        public Task LaunchPreparedAsync(
            PreparedApplicationUpdate update,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The coordinator test never launches an updater.");

        public async Task WaitForCallAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (CheckCalls < count)
            {
                await _entered.WaitAsync(timeout.Token);
            }
        }

        public void ReleaseOne() => _release.Release();

        public void Dispose()
        {
            _entered.Dispose();
            _release.Dispose();
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentChecks);
                if (value <= current ||
                    Interlocked.CompareExchange(
                        ref _maximumConcurrentChecks,
                        value,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class ManualUpdateClock : IUpdateMonitorClock
    {
        private readonly Channel<DelayRequest> _delays =
            Channel.CreateUnbounded<DelayRequest>();
        private int _delayCalls;

        public int DelayCalls => Volatile.Read(ref _delayCalls);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _delayCalls);
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await _delays.Writer.WriteAsync(
                new DelayRequest(delay, completion),
                cancellationToken);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                completion);
            await completion.Task;
        }

        public async Task<TimeSpan> AdvanceAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var request = await _delays.Reader.ReadAsync(timeout.Token);
            request.Completion.TrySetResult();
            return request.Delay;
        }

        private sealed record DelayRequest(TimeSpan Delay, TaskCompletionSource Completion);
    }

    private sealed record UpdateFixture(
        ApplicationUpdateCheck Check,
        PreparedApplicationUpdate Prepared);
}
