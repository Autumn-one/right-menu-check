using System.IO;
using System.Text;
using System.Text.Json;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.App.Services;

public interface IAnnouncementStateStore
{
    Task<IReadOnlyDictionary<string, int>> ReadAsync(CancellationToken cancellationToken);

    Task MarkShownAsync(string id, int revision, CancellationToken cancellationToken);
}

public sealed class AnnouncementStateStore : IAnnouncementStateStore, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IAppLogger _logger;
    private readonly string _path;

    public AnnouncementStateStore(IAppLogger logger, string? path = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RightMenuCheck",
            "Distribution",
            "announcement-state.json"));
    }

    public async Task<IReadOnlyDictionary<string, int>> ReadAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkShownAsync(
        string id,
        int revision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = new Dictionary<string, int>(
                await ReadCoreAsync(cancellationToken).ConfigureAwait(false),
                StringComparer.Ordinal);
            state[id] = Math.Max(state.GetValueOrDefault(id), revision);
            await WriteCoreAsync(new AnnouncementReceiptState(state), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<IReadOnlyDictionary<string, int>> ReadCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            var state = DistributionJson.Deserialize<AnnouncementReceiptState>(json);
            return new Dictionary<string, int>(state.Revisions, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is
                                           IOException or
                                           InvalidDataException or
                                           JsonException)
        {
            _logger.Log(
                AppLogLevel.Warning,
                "announcement.state_read_failed",
                "Announcement receipt state could not be read.",
                exception: exception);
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    private async Task WriteCoreAsync(
        AnnouncementReceiptState state,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    DistributionJson.Serialize(state, writeIndented: true),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record AnnouncementReceiptState(IReadOnlyDictionary<string, int> Revisions);
}
