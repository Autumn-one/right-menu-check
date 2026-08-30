using RightMenuCheck.Core.Backup;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Backup;
using RightMenuCheck.Windows.Registry;
using RightMenuCheck.Windows.Tests.Registry;

namespace RightMenuCheck.Windows.Tests.Backup;

public sealed class RegistrySnapshotReaderTests
{
    private const RegistryViewKind View = RegistryViewKind.Registry64;
    private const string RootPath = "Software\\Classes\\*\\shell\\sample";
    private static readonly string[] MultiValue = ["first", "second"];

    [Fact]
    public void CapturePreservesEmptyKeysChildrenTypesAndUnexpandedText()
    {
        var registry = new InMemoryRegistryReader(View);
        registry.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            RootPath,
            valueName: null,
            "%ProgramFiles%\\Sample\\sample.exe",
            RegistryValueDataKind.ExpandableText);
        registry.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            RootPath,
            "Multi",
            MultiValue,
            RegistryValueDataKind.MultiText);
        registry.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            RootPath,
            "Binary",
            new byte[] { 0, 1, 254, 255 },
            RegistryValueDataKind.Binary);
        registry.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            RootPath,
            "Dword",
            42,
            RegistryValueDataKind.DWord);
        registry.SetValue(
            RegistryHiveKind.LocalMachine,
            View,
            RootPath,
            "Qword",
            9_000_000_000L,
            RegistryValueDataKind.QWord);
        registry.AddKey(
            RegistryHiveKind.LocalMachine,
            View,
            $"{RootPath}\\empty-child");
        var reader = new RegistrySnapshotReader(
            registry,
            new FakeSecurityDescriptorReader());

        var result = reader.Capture(new RegistrySource(
            RegistryHiveKind.LocalMachine,
            View,
            RootPath));

        Assert.True(result.IsComplete);
        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Keys.Count);
        var root = Assert.Single(result.Keys, key => key.Source.KeyPath == RootPath);
        var child = Assert.Single(
            result.Keys,
            key => key.Source.KeyPath.EndsWith("empty-child", StringComparison.Ordinal));
        Assert.Empty(child.Values);
        Assert.NotNull(root.SecurityDescriptorSddl);
        Assert.Equal(5, root.Values.Count);
        var expandable = Assert.Single(root.Values, value => value.Name == string.Empty);
        Assert.Equal(BackupRegistryValueKind.ExpandableText, expandable.Kind);
        Assert.Equal("%ProgramFiles%\\Sample\\sample.exe", expandable.Text);
        Assert.Equal(
            "AAH+/w==",
            Assert.Single(root.Values, value => value.Name == "Binary").Base64Data);
        Assert.Equal(42, Assert.Single(root.Values, value => value.Name == "Dword").NumericValue);
        Assert.Equal(
            9_000_000_000L,
            Assert.Single(root.Values, value => value.Name == "Qword").NumericValue);
        Assert.Equal(
            ["first", "second"],
            Assert.Single(root.Values, value => value.Name == "Multi").TextItems);
    }

    [Fact]
    public void CaptureMarksMissingRootIncomplete()
    {
        var reader = new RegistrySnapshotReader(
            new InMemoryRegistryReader(View),
            new FakeSecurityDescriptorReader());

        var result = reader.Capture(new RegistrySource(
            RegistryHiveKind.LocalMachine,
            View,
            RootPath));

        Assert.False(result.IsComplete);
        Assert.Empty(result.Keys);
        Assert.Contains(result.Issues, issue => issue.ErrorType == "KeyNotFound");
    }
}
