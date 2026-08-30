using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RightMenuCheck.Distribution;
using RightMenuCheck.Windows.Diagnostics;

namespace RightMenuCheck.App.Services;

public interface IDistributionDocumentClient
{
    Task<T?> FetchVerifiedAsync<T>(
        IReadOnlyList<string> candidates,
        string cachePath,
        Func<T, bool> validator,
        Func<T, long> sequenceSelector,
        CancellationToken cancellationToken)
        where T : class;

    Task<string> DownloadPackageAsync(
        UpdatePackage package,
        string destinationDirectory,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

public sealed class DistributionDocumentClient : IDistributionDocumentClient
{
    private const int MaximumDocumentBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient _httpClient;
    private readonly IAppLogger _logger;

    public DistributionDocumentClient(HttpClient httpClient, IAppLogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<T?> FetchVerifiedAsync<T>(
        IReadOnlyList<string> candidates,
        string cachePath,
        Func<T, bool> validator,
        Func<T, long> sequenceSelector,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(sequenceSelector);

        var best = TryReadCache(cachePath, validator);
        var bestSequence = best is null ? 0 : sequenceSelector(best);
        if (bestSequence <= 0)
        {
            best = null;
            bestSequence = 0;
        }

        var fetches = candidates
            .Select(candidate => FetchCandidateAsync<T>(candidate, validator, cancellationToken))
            .ToArray();
        var sources = await Task.WhenAll(fetches).ConfigureAwait(false);
        var updated = false;
        foreach (var source in sources.Where(static source => source is not null))
        {
            var sequence = sequenceSelector(source!.Document);
            if (sequence <= 0)
            {
                LogSourceFailure(source.Source, "InvalidSequence");
                continue;
            }

            if (sequence > bestSequence)
            {
                best = source.Document;
                bestSequence = sequence;
                updated = true;
            }
            else if (sequence < bestSequence)
            {
                LogRollbackAttempt(source.Source, sequence, bestSequence);
            }
        }

        if (updated && best is not null)
        {
            TryWriteCache(cachePath, DistributionJson.Serialize(best, writeIndented: true));
        }

        return best;
    }

    public async Task<string> DownloadPackageAsync(
        UpdatePackage package,
        string destinationDirectory,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, package.AssetName);
        var sources = package.MirrorUrls
            .Append(package.PrimaryUrl)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partialPath = $"{destination}.partial-{Guid.NewGuid():N}";
            try
            {
                await DownloadAndVerifyPackageAsync(
                        source,
                        partialPath,
                        package,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                File.Move(partialPath, destination, overwrite: true);
                return destination;
            }
            catch (Exception exception) when (IsRecoverableNetworkFailure(exception, cancellationToken))
            {
                TryDeleteFile(partialPath);
                LogSourceFailure(source, exception.GetType().Name);
            }
            catch (InvalidDataException exception)
            {
                TryDeleteFile(partialPath);
                LogSourceFailure(source, exception.GetType().Name);
            }
        }

        throw new IOException("No update source produced the signed package.");
    }

    private async Task<string> DownloadDocumentAsync(
        string candidate,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);
        using var response = await _httpClient.GetAsync(
                candidate,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDocumentBytes)
        {
            throw new InvalidDataException("Distribution document exceeds the size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token)
            .ConfigureAwait(false);
        return Encoding.UTF8.GetString(
            await ReadLimitedAsync(stream, MaximumDocumentBytes, timeoutSource.Token)
                .ConfigureAwait(false));
    }

    private async Task<VerifiedSource<T>?> FetchCandidateAsync<T>(
        string candidate,
        Func<T, bool> validator,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var json = await DownloadDocumentAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            var document = DistributionJson.Deserialize<T>(json);
            if (!validator(document))
            {
                LogSourceFailure(candidate, "SignatureValidationFailed");
                return null;
            }

            return new VerifiedSource<T>(candidate, document);
        }
        catch (Exception exception) when (IsRecoverableNetworkFailure(exception, cancellationToken))
        {
            LogSourceFailure(candidate, exception.GetType().Name);
            return null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            LogSourceFailure(candidate, exception.GetType().Name);
            return null;
        }
    }

    private async Task DownloadAndVerifyPackageAsync(
        string source,
        string partialPath,
        UpdatePackage package,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await _httpClient.GetAsync(
                source,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != package.SizeBytes)
        {
            throw new InvalidDataException("Update source returned an unexpected package size.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(timeoutSource.Token)
            .ConfigureAwait(false);
        await using var output = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long totalBytes = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, timeoutSource.Token).ConfigureAwait(false)) > 0)
            {
                totalBytes = checked(totalBytes + read);
                if (totalBytes > package.SizeBytes)
                {
                    throw new InvalidDataException("Update source exceeded the signed package size.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token)
                    .ConfigureAwait(false);
                progress?.Report(totalBytes / (double)package.SizeBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await output.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
        if (totalBytes != package.SizeBytes ||
            !Convert.ToHexString(hash.GetHashAndReset()).Equals(
                package.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update package integrity verification failed.");
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidDataException("Distribution document exceeds the size limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private T? TryReadCache<T>(string cachePath, Func<T, bool> validator)
        where T : class
    {
        try
        {
            if (!File.Exists(cachePath))
            {
                return null;
            }

            var document = DistributionJson.Deserialize<T>(File.ReadAllText(cachePath));
            return validator(document) ? document : null;
        }
        catch (Exception exception) when (exception is
                                           IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           JsonException)
        {
            _logger.Log(
                AppLogLevel.Warning,
                "distribution.cache_read_failed",
                "Verified distribution cache could not be read.",
                exception: exception);
            return null;
        }
    }

    private void TryWriteCache(string cachePath, string json)
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(cachePath))!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (temporaryPath is not null)
            {
                TryDeleteFile(temporaryPath);
            }

            _logger.Log(
                AppLogLevel.Warning,
                "distribution.cache_write_failed",
                "Verified distribution cache could not be written.",
                exception: exception);
        }
    }

    private static bool IsRecoverableNetworkFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        exception is HttpRequestException or IOException ||
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private void LogSourceFailure(string source, string errorType)
    {
        var host = Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri.Host : "invalid";
        _logger.Log(
            AppLogLevel.Warning,
            "distribution.source_failed",
            "Distribution source did not produce a valid response.",
            new Dictionary<string, object?>
            {
                ["host"] = host,
                ["errorType"] = errorType,
            });
    }

    private void LogRollbackAttempt(string source, long sequence, long highestSequence)
    {
        var host = Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri.Host : "invalid";
        _logger.Log(
            AppLogLevel.Warning,
            "distribution.rollback_ignored",
            "A lower-sequence signed distribution document was ignored.",
            new Dictionary<string, object?>
            {
                ["host"] = host,
                ["sequence"] = sequence,
                ["highestSequence"] = highestSequence,
            });
    }

    private static void TryDeleteFile(string path)
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

    private sealed record VerifiedSource<T>(string Source, T Document)
        where T : class;
}
