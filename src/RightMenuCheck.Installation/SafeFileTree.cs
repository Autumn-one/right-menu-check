namespace RightMenuCheck.Installation;

public static class SafeFileTree
{
    private const int MaximumDepth = 64;

    public static void EnsureDirectoryIsNotReparsePoint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"Directory cannot be a filesystem link or reparse point: {directory.FullName}");
        }
    }

    public static void DeleteDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists)
        {
            return;
        }

        if (directory.Parent is null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "Refusing to recursively delete a root or reparse-point directory.");
        }

        DeleteContents(directory, depth: 0);
        directory.Delete(recursive: false);
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectory(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void TryDeleteFile(string path)
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

    private static void DeleteContents(DirectoryInfo directory, int depth)
    {
        if (depth >= MaximumDepth)
        {
            throw new InvalidDataException("Directory tree exceeds the supported depth.");
        }

        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (entry is DirectoryInfo linkedDirectory)
                {
                    linkedDirectory.Delete(recursive: false);
                }
                else
                {
                    entry.Delete();
                }

                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                DeleteContents(childDirectory, depth + 1);
                childDirectory.Delete(recursive: false);
                continue;
            }

            entry.Attributes = FileAttributes.Normal;
            entry.Delete();
        }
    }
}
