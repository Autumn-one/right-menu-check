using RightMenuCheck.App.Services;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.IntegrationTests.App;

public sealed class ContextMenuDataServiceCancellationTests
{
    [Fact]
    public async Task CanceledScanIsNotLoggedAsFailure()
    {
        using var logger = new RecordingLogger();
        var service = new ContextMenuDataService(logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ScanAsync(progress: null, cancellation.Token));

        Assert.Contains(
            logger.Events,
            entry => entry is (AppLogLevel.Information, "scan.started"));
        Assert.Contains(
            logger.Events,
            entry => entry is (AppLogLevel.Information, "scan.canceled"));
        Assert.DoesNotContain(logger.Events, entry => entry.EventName == "scan.failed");
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<(AppLogLevel Level, string EventName)> Events { get; } = [];

        public void Log(
            AppLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? properties = null,
            Exception? exception = null)
        {
            _ = message;
            _ = properties;
            _ = exception;
            Events.Add((level, eventName));
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
