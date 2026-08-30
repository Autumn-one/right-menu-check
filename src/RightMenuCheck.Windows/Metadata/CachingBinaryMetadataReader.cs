using System.Collections.Concurrent;
using RightMenuCheck.Core.Metadata;

namespace RightMenuCheck.Windows.Metadata;

public sealed class CachingBinaryMetadataReader : IBinaryMetadataReader
{
    private readonly ConcurrentDictionary<string, Lazy<BinaryFileMetadata>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IBinaryMetadataReader _inner;

    public CachingBinaryMetadataReader(IBinaryMetadataReader inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public BinaryFileMetadata Read(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var key = TryNormalizePath(filePath);
        return _cache.GetOrAdd(
            key,
            path => new Lazy<BinaryFileMetadata>(
                () => _inner.Read(path),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static string TryNormalizePath(string filePath)
    {
        try
        {
            return Path.GetFullPath(filePath);
        }
        catch (ArgumentException)
        {
            return filePath;
        }
        catch (NotSupportedException)
        {
            return filePath;
        }
        catch (PathTooLongException)
        {
            return filePath;
        }
    }
}
