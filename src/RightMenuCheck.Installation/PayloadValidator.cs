using System.Text.Json;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.Installation;

public sealed record ValidatedPayload(
    string Version,
    string ApplicationPath,
    long SizeBytes);

public static class PayloadValidator
{
    private const long MaximumBuildInfoBytes = 64 * 1024;
    private static readonly string[] RequiredRelativePaths =
    [
        "RightMenuCheck.App.exe",
        "build-info.json",
        Path.Combine("helpers", "RightMenuCheck.Elevated.exe"),
        Path.Combine("helpers", "updater", "RightMenuCheck.Updater.exe"),
        Path.Combine("workers", "x64", "RightMenuCheck.Probe.Worker.exe"),
        Path.Combine("workers", "x86", "RightMenuCheck.Probe.Worker.exe"),
        Path.Combine("workers", "arm64", "RightMenuCheck.Probe.Worker.exe"),
    ];

    public static ValidatedPayload Validate(string payloadDirectory, string expectedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        _ = SemanticVersion.Parse(expectedVersion);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadDirectory));
        SafeFileTree.EnsureDirectoryIsNotReparsePoint(root);
        foreach (var relativePath in RequiredRelativePaths)
        {
            if (!File.Exists(Path.Combine(root, relativePath)))
            {
                throw new InvalidDataException(
                    $"Installer payload is missing required file: {relativePath}");
            }
        }

        long sizeBytes = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Installer payload contains a filesystem link.");
            }

            if (file.Name.Equals("github-conf.json", StringComparison.OrdinalIgnoreCase) ||
                file.Name.Equals("maidian.json", StringComparison.OrdinalIgnoreCase) ||
                file.FullName.Split(Path.DirectorySeparatorChar).Any(static segment =>
                    segment.Equals(".secrets", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Installer payload contains private configuration.");
            }

            checked
            {
                sizeBytes += file.Length;
            }
        }

        var buildInfoPath = Path.Combine(root, "build-info.json");
        using var stream = new FileStream(
            buildInfoPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumBuildInfoBytes)
        {
            throw new InvalidDataException("Installer payload build identity size is invalid.");
        }

        BuildInfo? buildInfo;
        try
        {
            buildInfo = JsonSerializer.Deserialize<BuildInfo>(stream, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Installer payload build identity is invalid.", exception);
        }

        if (buildInfo is not
            {
                Product: "RightMenuCheck",
                Version: { Length: > 0 } actualVersion,
                SelfContained: true,
            } ||
            SemanticVersion.Parse(actualVersion).CompareTo(
                SemanticVersion.Parse(expectedVersion)) != 0)
        {
            throw new InvalidDataException(
                "Installer payload version or product identity does not match the setup package.");
        }

        return new ValidatedPayload(
            actualVersion,
            Path.Combine(root, UpdateInstallLocations.ApplicationFileName),
            sizeBytes);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record BuildInfo(string Product, string Version, bool SelfContained);
}
