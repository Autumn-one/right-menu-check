using System.Text.Json;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.Windows.Tests.Diagnostics;

public sealed class StructuredFileLoggerTests
{
    [Fact]
    public async Task LogWritesOnDedicatedThreadFlushesAndRedactsPaths()
    {
        var directory = CreateTemporaryDirectory();
        var callerThreadId = Environment.CurrentManagedThreadId;
        try
        {
            using (var logger = new StructuredFileLogger("test", directory))
            {
                logger.Log(
                    AppLogLevel.Error,
                    "test.failure",
                    "Failed at C:\\Secret\\private.txt",
                    new Dictionary<string, object?>
                    {
                        ["targetPath"] = "C:\\Secret\\private.txt",
                        ["count"] = 3,
                    },
                    new IOException("Cannot read C:\\Secret\\private.txt"));
                await logger.FlushAsync(CancellationToken.None);
                Assert.NotEqual(0, logger.LoggingThreadId);
                Assert.NotEqual(callerThreadId, logger.LoggingThreadId);
            }

            var file = Assert.Single(Directory.GetFiles(directory, "*.jsonl"));
            var content = File.ReadAllText(file);
            Assert.DoesNotContain("C:\\Secret", content, StringComparison.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(content.Trim());
            var root = document.RootElement;
            Assert.Equal("test.failure", root.GetProperty("eventName").GetString());
            Assert.Contains(
                "<path>",
                root.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.NotEqual(
                root.GetProperty("callerThreadId").GetInt32(),
                root.GetProperty("writerThreadId").GetInt32());
            Assert.Equal(
                "<redacted>",
                root.GetProperty("properties").GetProperty("targetPath").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LogRollsToAdditionalFilesAtConfiguredSize()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using (var logger = new StructuredFileLogger(
                       "roll",
                       directory,
                       maximumFileBytes: 512))
            {
                for (var index = 0; index < 30; index++)
                {
                    logger.Log(
                        AppLogLevel.Information,
                        "test.roll",
                        new string('x', 100),
                        new Dictionary<string, object?> { ["index"] = index });
                }

                await logger.FlushAsync(CancellationToken.None);
            }

            Assert.True(Directory.GetFiles(directory, "*.jsonl").Length > 1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"RightMenuCheck-Logger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
