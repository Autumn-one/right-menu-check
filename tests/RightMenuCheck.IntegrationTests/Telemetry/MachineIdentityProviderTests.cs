using System.Security.Cryptography;
using System.Text;
using RightMenuCheck.App.Services;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.IntegrationTests.Telemetry;

public sealed class MachineIdentityProviderTests
{
    [Fact]
    public async Task MachineGuidUsesStableApplicationNamespacedHashWithoutWritingTheGuid()
    {
        var directory = CreateTemporaryDirectory();
        var seedPath = Path.Combine(directory, "identity.seed");
        const string machineGuid = " D8B54E25-67FA-45B7-B47B-3EC8A8A47F11 ";

        try
        {
            var provider = new MachineIdentityProvider(seedPath, () => machineGuid);

            var first = await provider.GetMachineIdAsync();
            var second = await provider.GetMachineIdAsync();

            Assert.Equal(ExpectedMachineGuidHash(machineGuid), first);
            Assert.Equal(first, second);
            Assert.True(TelemetryIdentityValidator.IsValidMachineId(first));
            Assert.False(File.Exists(seedPath));
            Assert.DoesNotContain(
                machineGuid.Trim(),
                first,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryFailurePersistsAndReusesRandomFallbackSeed()
    {
        var directory = CreateTemporaryDirectory();
        var seedPath = Path.Combine(directory, "identity.seed");
        var logger = new RecordingLogger();

        try
        {
            var firstProvider = new MachineIdentityProvider(
                seedPath,
                static () => throw new UnauthorizedAccessException("fixture-secret"),
                logger);
            var secondProvider = new MachineIdentityProvider(
                seedPath,
                static () => throw new UnauthorizedAccessException("fixture-secret"),
                logger);

            var first = await firstProvider.GetMachineIdAsync();
            var second = await secondProvider.GetMachineIdAsync();

            Assert.Equal(first, second);
            Assert.True(TelemetryIdentityValidator.IsValidMachineId(first));
            Assert.Equal(32, new FileInfo(seedPath).Length);
            Assert.Equal(ExpectedSeedHash(await File.ReadAllBytesAsync(seedPath)), first);
            Assert.All(
                logger.Events,
                entry => Assert.DoesNotContain(
                    "fixture-secret",
                    entry.Rendered,
                    StringComparison.Ordinal));
            Assert.Contains(
                logger.Events,
                entry => entry.EventName == "telemetry.identity_fallback" &&
                         entry.Rendered.Contains(
                             nameof(UnauthorizedAccessException),
                             StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string ExpectedMachineGuidHash(string machineGuid)
    {
        var canonical = Guid.Parse(machineGuid.Trim()).ToString("D");
        return ComputeExpectedHash("machine-guid", Encoding.UTF8.GetBytes(canonical));
    }

    private static string ExpectedSeedHash(byte[] seed) =>
        ComputeExpectedHash("local-seed", seed);

    private static string ComputeExpectedHash(string sourceKind, ReadOnlySpan<byte> source)
    {
        var domain = Encoding.UTF8.GetBytes(
            $"{MachineIdentityProvider.IdentityHashNamespace}\0{sourceKind}\0");
        var hashInput = new byte[domain.Length + source.Length];
        domain.CopyTo(hashInput, 0);
        source.CopyTo(hashInput.AsSpan(domain.Length));
        return Convert.ToHexString(SHA256.HashData(hashInput)).ToLowerInvariant();
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "RightMenuCheck-TelemetryTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<LogEntry> Events { get; } = [];

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
            Events.Add(new LogEntry(eventName, $"{message}|{propertyText}"));
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed record LogEntry(string EventName, string Rendered);
}
