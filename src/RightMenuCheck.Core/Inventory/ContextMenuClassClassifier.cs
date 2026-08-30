namespace RightMenuCheck.Core.Inventory;

public static class ContextMenuClassClassifier
{
    public static ContextMenuTargetKind Classify(string classPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classPath);

        var normalized = classPath.Replace('/', '\\').Trim('\\');

        if (normalized.Equals("*", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("SystemFileAssociations\\*", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuTargetKind.File;
        }

        if (normalized.Equals("AllFilesystemObjects", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuTargetKind.AllFileSystemObjects;
        }

        if (normalized.Equals("Directory", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Folder", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuTargetKind.Folder;
        }

        if (normalized.Equals("Directory\\Background", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuTargetKind.FolderBackground;
        }

        if (normalized.Equals("Drive", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuTargetKind.Drive;
        }

        if (normalized.Equals("DesktopBackground", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuTargetKind.DesktopBackground;
        }

        if (normalized.Equals("LibraryFolder", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuTargetKind.LibraryFolder;
        }

        if (normalized.Equals("LibraryFolder\\Background", StringComparison.OrdinalIgnoreCase))
        {
            return ContextMenuTargetKind.LibraryBackground;
        }

        if (normalized.StartsWith("SystemFileAssociations\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith('.'))
        {
            return ContextMenuTargetKind.FileType;
        }

        return normalized.Length == 0
            ? ContextMenuTargetKind.Unknown
            : ContextMenuTargetKind.FileType;
    }
}
