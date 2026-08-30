using System.IO.Compression;
using RightMenuCheck.Updater;

namespace RightMenuCheck.Updater.Tests;

public sealed class SafeZipExtractorTests
{
    [Fact]
    public async Task ExtractsNormalizedFiles()
    {
        using var fixture = new TemporaryDirectory();
        var package = Path.Combine(fixture.Path, "package.zip");
        CreateArchive(package, ("nested/file.txt", "content"));
        var destination = Path.Combine(fixture.Path, "staging");

        await new SafeZipExtractor().ExtractAsync(package, destination, CancellationToken.None);

        Assert.Equal("content", File.ReadAllText(Path.Combine(destination, "nested", "file.txt")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData("/absolute.txt")]
    public async Task RejectsArchivePathTraversal(string entryName)
    {
        using var fixture = new TemporaryDirectory();
        var package = Path.Combine(fixture.Path, "unsafe.zip");
        CreateArchive(package, (entryName, "bad"));
        var destination = Path.Combine(fixture.Path, "staging");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SafeZipExtractor().ExtractAsync(package, destination, CancellationToken.None));

        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "outside.txt")));
    }

    internal static void CreateArchive(string path, params (string Name, string Content)[] files)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(file.Name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(file.Content);
        }
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RightMenuCheck-Updater-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
