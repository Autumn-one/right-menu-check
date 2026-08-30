using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.App.Services;

public interface IMachineIdentityProvider
{
    ValueTask<string> GetMachineIdAsync(CancellationToken cancellationToken = default);
}

public sealed class MachineIdentityProvider : IMachineIdentityProvider
{
    public const string IdentityHashNamespace =
        "RightMenuCheck.Telemetry.MachineIdentity/v1";

    private const int FallbackSeedLength = 32;
    private const int SeedFileOpenAttempts = 5;
    private static readonly TimeSpan SeedFileRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly object _cacheGate = new();
    private readonly string _fallbackSeedPath;
    private readonly Func<string?> _machineGuidReader;
    private readonly IAppLogger _logger;
    private Task<string>? _machineIdTask;

    public MachineIdentityProvider(IAppLogger? logger = null)
        : this(GetDefaultFallbackSeedPath(), ReadSystemMachineGuid, logger)
    {
    }

    public MachineIdentityProvider(
        string fallbackSeedPath,
        Func<string?> machineGuidReader,
        IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackSeedPath);
        ArgumentNullException.ThrowIfNull(machineGuidReader);

        _fallbackSeedPath = Path.GetFullPath(fallbackSeedPath);
        _machineGuidReader = machineGuidReader;
        _logger = logger ?? NullAppLogger.Instance;
    }

    public async ValueTask<string> GetMachineIdAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<string> identityTask;
        lock (_cacheGate)
        {
            _machineIdTask ??= Task.Run(ResolveMachineIdAsync, CancellationToken.None);
            identityTask = _machineIdTask;
        }

        return await identityTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveMachineIdAsync()
    {
        string? machineGuid = null;
        try
        {
            machineGuid = _machineGuidReader();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                          SecurityException or
                                          IOException or
                                          PlatformNotSupportedException)
        {
            LogFallback("machine_guid_read_failed", exception);
        }

        if (!string.IsNullOrWhiteSpace(machineGuid))
        {
            var canonicalMachineGuid = CanonicalizeMachineGuid(machineGuid);
            return HashIdentitySource("machine-guid", Encoding.UTF8.GetBytes(canonicalMachineGuid));
        }

        LogFallback(
            machineGuid is null ? "machine_guid_missing" : "machine_guid_empty",
            exception: null);

        var seed = await ReadOrCreateFallbackSeedAsync().ConfigureAwait(false);
        return HashIdentitySource("local-seed", seed);
    }

    private async Task<byte[]> ReadOrCreateFallbackSeedAsync()
    {
        var directory = Path.GetDirectoryName(_fallbackSeedPath) ??
                        throw new InvalidOperationException(
                            "The telemetry identity seed path has no parent directory.");
        Directory.CreateDirectory(directory);

        for (var attempt = 1; attempt <= SeedFileOpenAttempts; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    _fallbackSeedPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: FallbackSeedLength,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);

                if (stream.Length == FallbackSeedLength)
                {
                    var existingSeed = new byte[FallbackSeedLength];
                    await stream.ReadExactlyAsync(existingSeed, CancellationToken.None)
                        .ConfigureAwait(false);
                    return existingSeed;
                }

                var newSeed = RandomNumberGenerator.GetBytes(FallbackSeedLength);
                stream.SetLength(0);
                await stream.WriteAsync(newSeed, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                return newSeed;
            }
            catch (IOException) when (attempt < SeedFileOpenAttempts)
            {
                await Task.Delay(SeedFileRetryDelay, CancellationToken.None).ConfigureAwait(false);
            }
        }

        throw new IOException("The telemetry identity seed file could not be opened.");
    }

    private void LogFallback(string reason, Exception? exception)
    {
        _logger.Log(
            AppLogLevel.Warning,
            "telemetry.identity_fallback",
            "A local random seed will be used for the telemetry identity.",
            new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["exceptionType"] = exception?.GetType().Name,
                ["exceptionHResult"] = exception?.HResult,
            });
    }

    private static string CanonicalizeMachineGuid(string value)
    {
        var trimmed = value.Trim();
        return Guid.TryParse(trimmed, out var parsed)
            ? parsed.ToString("D", CultureInfo.InvariantCulture)
            : trimmed.ToUpperInvariant();
    }

    private static string HashIdentitySource(string sourceKind, ReadOnlySpan<byte> source)
    {
        var domain = Encoding.UTF8.GetBytes($"{IdentityHashNamespace}\0{sourceKind}\0");
        var hashInput = new byte[domain.Length + source.Length];
        domain.CopyTo(hashInput, 0);
        source.CopyTo(hashInput.AsSpan(domain.Length));
        return Convert.ToHexString(SHA256.HashData(hashInput)).ToLowerInvariant();
    }

    private static string GetDefaultFallbackSeedPath()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "The local application data directory is unavailable.");
        }

        return Path.Combine(
            localApplicationData,
            "RightMenuCheck",
            "Telemetry",
            "identity.seed");
    }

    private static string? ReadSystemMachineGuid()
    {
        using var localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var cryptography = localMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Cryptography",
            writable: false);
        return cryptography?.GetValue(
            "MachineGuid",
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
}
