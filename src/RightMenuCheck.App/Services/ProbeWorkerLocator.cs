using System.IO;
using RightMenuCheck.Windows.Probe;

namespace RightMenuCheck.App.Services;

internal static class ProbeWorkerLocator
{
    private const string WorkerFileName = "RightMenuCheck.Probe.Worker.exe";

    public static ProbeWorkerPaths Locate()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var artifactsRoot = FindArtifactsRoot(baseDirectory);
        var x64Candidates = new List<string>
        {
            Path.Combine(baseDirectory, "workers", "x64", WorkerFileName),
        };
        var x86Candidates = new List<string>
        {
            Path.Combine(baseDirectory, "workers", "x86", WorkerFileName),
        };
        var arm64Candidates = new List<string>
        {
            Path.Combine(baseDirectory, "workers", "arm64", WorkerFileName),
        };

        if (artifactsRoot is not null)
        {
            x64Candidates.Add(Path.Combine(
                artifactsRoot,
                "publish",
                "RightMenuCheck.Probe.Worker",
                "release_win-x64",
                WorkerFileName));
            x64Candidates.Add(Path.Combine(
                artifactsRoot,
                "bin",
                "RightMenuCheck.Probe.Worker",
                "debug",
                WorkerFileName));
            x86Candidates.Add(Path.Combine(
                artifactsRoot,
                "publish",
                "RightMenuCheck.Probe.Worker",
                "release_win-x86",
                WorkerFileName));
            arm64Candidates.Add(Path.Combine(
                artifactsRoot,
                "publish",
                "RightMenuCheck.Probe.Worker",
                "release_win-arm64",
                WorkerFileName));
        }

        return new ProbeWorkerPaths(
            FindExistingOrFirst(x64Candidates),
            FindExistingOrFirst(x86Candidates),
            arm64Candidates.FirstOrDefault(File.Exists));
    }

    private static string FindExistingOrFirst(List<string> candidates) =>
        candidates.FirstOrDefault(File.Exists) ?? candidates[0];

    private static string? FindArtifactsRoot(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null)
        {
            if (directory.Name.Equals("artifacts", StringComparison.OrdinalIgnoreCase))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
