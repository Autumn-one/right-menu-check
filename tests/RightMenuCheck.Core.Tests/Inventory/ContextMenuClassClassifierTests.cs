using RightMenuCheck.Core.Inventory;

namespace RightMenuCheck.Core.Tests.Inventory;

public sealed class ContextMenuClassClassifierTests
{
    [Theory]
    [InlineData("*", ContextMenuTargetKind.File)]
    [InlineData("SystemFileAssociations\\*", ContextMenuTargetKind.File)]
    [InlineData("AllFilesystemObjects", ContextMenuTargetKind.AllFileSystemObjects)]
    [InlineData("Directory", ContextMenuTargetKind.Folder)]
    [InlineData("Folder", ContextMenuTargetKind.Folder)]
    [InlineData("Directory\\Background", ContextMenuTargetKind.FolderBackground)]
    [InlineData("Drive", ContextMenuTargetKind.Drive)]
    [InlineData("DesktopBackground", ContextMenuTargetKind.DesktopBackground)]
    [InlineData("LibraryFolder", ContextMenuTargetKind.LibraryFolder)]
    [InlineData("LibraryFolder\\background", ContextMenuTargetKind.LibraryBackground)]
    [InlineData(".txt", ContextMenuTargetKind.FileType)]
    [InlineData("SystemFileAssociations\\image", ContextMenuTargetKind.FileType)]
    [InlineData("txtfile", ContextMenuTargetKind.FileType)]
    public void ClassifyReturnsExpectedTarget(string classPath, ContextMenuTargetKind expected)
    {
        Assert.Equal(expected, ContextMenuClassClassifier.Classify(classPath));
    }

    [Fact]
    public void ClassifyNormalizesForwardSlashesAndOuterSeparators()
    {
        var actual = ContextMenuClassClassifier.Classify("\\Directory/Background\\");

        Assert.Equal(ContextMenuTargetKind.FolderBackground, actual);
    }
}
