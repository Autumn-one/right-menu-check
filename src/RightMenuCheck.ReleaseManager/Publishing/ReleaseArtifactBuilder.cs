using System.IO.Compression;
using System.Security.Cryptography;

namespace RightMenuCheck.ReleaseManager.Publishing;

public sealed record ReleaseArtifact(
    string ArchivePath,
    string AssetName,
    long SizeBytes,
    string Sha256);

public interface IReleaseArtifactBuilder
{
    Task<ReleaseArtifact> BuildAsync(
        string sourceDirectory,
        string outputDirectory,
        string version,
        CancellationToken cancellationToken);
}

public sealed class ReleaseArtifactBuilder : IReleaseArtifactBuilder
{
    public async Task<ReleaseArtifact> BuildAsync(
        string sourceDirectory,
        string outputDirectory,
        string version,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException("固定发布目录不存在。");
        }

        var sourceWithSeparator = source.TrimEnd(Path.DirectorySeparatorChar) +
                                  Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase) ||
            destination.Equals(source, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("发布压缩包目录不能位于被压缩目录内部。");
        }

        ValidateSourceTree(source);
        Directory.CreateDirectory(destination);
        var assetName = $"RightMenuCheck-{version}-win-x64.zip";
        var archivePath = Path.Combine(destination, assetName);
        var temporaryPath = archivePath + ".tmp";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }

        try
        {
            await Task.Run(
                () => ZipFile.CreateFromDirectory(
                    source,
                    temporaryPath,
                    CompressionLevel.SmallestSize,
                    includeBaseDirectory: false),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, archivePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new ReleaseArtifact(
            archivePath,
            assetName,
            stream.Length,
            Convert.ToHexString(hash));
    }

    private static void ValidateSourceTree(string sourceDirectory)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(sourceDirectory);
        while (pendingDirectories.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                var relativePath = Path.GetRelativePath(sourceDirectory, entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"发布目录包含不允许的重解析链接：{relativePath}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (Path.GetFileName(entry).Equals(".secrets", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("发布目录包含不允许的 .secrets 目录。");
                    }

                    pendingDirectories.Push(entry);
                    continue;
                }

                var fileName = Path.GetFileName(entry);
                var extension = Path.GetExtension(fileName);
                if (fileName.Equals("github-conf.json", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".pem", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".key", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".p12", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"发布目录包含不允许的敏感文件：{relativePath}");
                }
            }
        }
    }
}
