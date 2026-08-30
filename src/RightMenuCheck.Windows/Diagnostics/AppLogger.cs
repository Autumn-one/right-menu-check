using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RightMenuCheck.Windows.Diagnostics;

public enum AppLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public interface IAppLogger : IDisposable
{
    void Log(
        AppLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null);

    Task FlushAsync(CancellationToken cancellationToken = default);
}

public sealed partial class StructuredFileLogger : IAppLogger
{
    private const int QueueCapacity = 4096;
    private const long DefaultMaximumFileBytes = 5L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly BlockingCollection<LogCommand> _queue = new(QueueCapacity);
    private readonly string _component;
    private readonly string _directory;
    private readonly long _maximumFileBytes;
    private readonly Thread _thread;
    private int _disposed;
    private int _droppedEvents;

    public StructuredFileLogger(
        string component,
        string directory,
        long maximumFileBytes = DefaultMaximumFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maximumFileBytes < 512)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFileBytes),
                maximumFileBytes,
                "Maximum log file size must be at least 512 bytes.");
        }

        _component = SanitizeComponent(component);
        _directory = Path.GetFullPath(directory);
        _maximumFileBytes = maximumFileBytes;
        Directory.CreateDirectory(_directory);
        _thread = new Thread(LoggingThreadMain)
        {
            IsBackground = true,
            Name = $"RightMenuCheck Log {_component}",
        };
        _thread.Start();
    }

    public int LoggingThreadId { get; private set; }

    public static IAppLogger CreateDefault(string component)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RightMenuCheck",
                "Logs");
            return new StructuredFileLogger(component, directory);
        }
        catch (UnauthorizedAccessException)
        {
            return NullAppLogger.Instance;
        }
        catch (IOException)
        {
            return NullAppLogger.Instance;
        }
    }

    public void Log(
        AppLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var command = new EventCommand(new PendingLogEvent(
            DateTimeOffset.UtcNow,
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            level,
            eventName,
            Redact(message),
            SanitizeProperties(properties),
            exception?.GetType().Name,
            exception?.HResult,
            exception is null ? null : Redact(exception.Message)));
        if (!_queue.TryAdd(command))
        {
            _ = Interlocked.Increment(ref _droppedEvents);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new FlushCommand(completion), cancellationToken);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _ = _thread.Join(millisecondsTimeout: 5000);
        _queue.Dispose();
    }

    private void LoggingThreadMain()
    {
        LoggingThreadId = Environment.CurrentManagedThreadId;
        StreamWriter? writer = null;
        DateOnly currentDate = default;
        var rollIndex = 0;

        try
        {
            foreach (var command in _queue.GetConsumingEnumerable())
            {
                try
                {
                    writer = EnsureWriter(writer, ref currentDate, ref rollIndex);
                    switch (command)
                    {
                        case EventCommand eventCommand:
                            WriteEvent(writer, eventCommand.Event);
                            break;
                        case FlushCommand flushCommand:
                            writer.Flush();
                            flushCommand.Completion.TrySetResult();
                            break;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (command is FlushCommand flushCommand)
                    {
                        flushCommand.Completion.TrySetException(exception);
                    }
                }
            }

            if (_droppedEvents > 0)
            {
                writer = EnsureWriter(writer, ref currentDate, ref rollIndex);
                WriteEvent(writer, new PendingLogEvent(
                    DateTimeOffset.UtcNow,
                    Environment.ProcessId,
                    Environment.CurrentManagedThreadId,
                    AppLogLevel.Warning,
                    "log.events_dropped",
                    "The bounded logging queue dropped events.",
                    new Dictionary<string, object?> { ["count"] = _droppedEvents },
                    ExceptionType: null,
                    ExceptionHResult: null,
                    ExceptionMessage: null));
            }

            writer?.Flush();
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private StreamWriter EnsureWriter(
        StreamWriter? writer,
        ref DateOnly currentDate,
        ref int rollIndex)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (writer is not null && currentDate == today &&
            writer.BaseStream.Length < _maximumFileBytes)
        {
            return writer;
        }

        if (writer is not null)
        {
            writer.Flush();
            writer.Dispose();
            rollIndex = currentDate == today ? rollIndex + 1 : 0;
        }

        currentDate = today;
        var fileName = $"{_component}-{today:yyyyMMdd}-{Environment.ProcessId}-{rollIndex:D3}.jsonl";
        var stream = new FileStream(
            Path.Combine(_directory, fileName),
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16384,
            FileOptions.SequentialScan);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void WriteEvent(StreamWriter writer, PendingLogEvent logEvent)
    {
        var record = new
        {
            logEvent.Timestamp,
            Component = _component,
            logEvent.ProcessId,
            logEvent.CallerThreadId,
            WriterThreadId = LoggingThreadId,
            Level = logEvent.Level.ToString(),
            logEvent.EventName,
            logEvent.Message,
            logEvent.Properties,
            logEvent.ExceptionType,
            logEvent.ExceptionHResult,
            logEvent.ExceptionMessage,
        };
        writer.WriteLine(JsonSerializer.Serialize(record, SerializerOptions));
        writer.Flush();
    }

    private static Dictionary<string, object?>? SanitizeProperties(
        IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null)
        {
            return null;
        }

        return properties.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Key.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                           pair.Key.Contains("command", StringComparison.OrdinalIgnoreCase)
                ? "<redacted>"
                : pair.Value is string text
                    ? Redact(text)
                    : pair.Value,
            StringComparer.Ordinal);
    }

    private static string SanitizeComponent(string component) =>
        new(component.Where(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .ToArray());

    private static string Redact(string value) =>
        WindowsPathRegex().Replace(value, "<path>");

    [GeneratedRegex(@"(?i)(?:[a-z]:\\|\\\\)[^\s\""']+")]
    private static partial Regex WindowsPathRegex();

    private abstract record LogCommand;

    private sealed record EventCommand(PendingLogEvent Event) : LogCommand;

    private sealed record FlushCommand(TaskCompletionSource Completion) : LogCommand;

    private sealed record PendingLogEvent(
        DateTimeOffset Timestamp,
        int ProcessId,
        int CallerThreadId,
        AppLogLevel Level,
        string EventName,
        string Message,
        IReadOnlyDictionary<string, object?>? Properties,
        string? ExceptionType,
        int? ExceptionHResult,
        string? ExceptionMessage);
}

public sealed class NullAppLogger : IAppLogger
{
    private NullAppLogger()
    {
    }

    public static NullAppLogger Instance { get; } = new();

    public void Log(
        AppLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? properties = null,
        Exception? exception = null)
    {
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
