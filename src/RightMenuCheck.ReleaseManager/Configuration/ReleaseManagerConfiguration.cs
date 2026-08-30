using System.Diagnostics;
using System.Text.Json;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.ReleaseManager.Configuration;

[DebuggerDisplay("{Repository} ({DefaultBranch})")]
public sealed class ReleaseManagerConfiguration
{
    public ReleaseManagerConfiguration(
        RepositoryCoordinates repository,
        string accessToken,
        string defaultBranch,
        IReadOnlyList<string> mirrorPrefixes,
        string signingPrivateKeyPath)
    {
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(signingPrivateKeyPath);

        AccessToken = accessToken.Trim();
        DefaultBranch = defaultBranch.Trim();
        MirrorPrefixes = mirrorPrefixes?.ToArray() ??
                         throw new ArgumentNullException(nameof(mirrorPrefixes));
        SigningPrivateKeyPath = signingPrivateKeyPath;

        _ = DistributionEndpoints.BuildRawContentCandidates(
            Repository,
            DefaultBranch,
            "distribution/update.json",
            MirrorPrefixes);
    }

    public RepositoryCoordinates Repository { get; }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    internal string AccessToken { get; }

    public string DefaultBranch { get; }

    public IReadOnlyList<string> MirrorPrefixes { get; }

    public string SigningPrivateKeyPath { get; }

    public static ReleaseManagerConfiguration Load(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        var configurationPath = Path.Combine(root, "github-conf.json");
        if (!File.Exists(configurationPath))
        {
            throw new FileNotFoundException(
                "未找到发布配置 github-conf.json。",
                configurationPath);
        }

        ConfigurationFile? document;
        try
        {
            document = JsonSerializer.Deserialize<ConfigurationFile>(
                File.ReadAllText(configurationPath),
                SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("github-conf.json 格式无效。", exception);
        }

        if (document is null)
        {
            throw new InvalidDataException("github-conf.json 不包含配置对象。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(document.Repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Token);
        var mirrors = document.Mirrors is { Count: > 0 }
            ? document.Mirrors
            : DistributionEndpoints.DefaultMirrorPrefixes;
        var keySetting = string.IsNullOrWhiteSpace(document.SigningPrivateKeyPath)
            ? Path.Combine(".secrets", "update-signing-private.pem")
            : document.SigningPrivateKeyPath.Trim();
        var keyPath = Path.IsPathRooted(keySetting)
            ? Path.GetFullPath(keySetting)
            : Path.GetFullPath(Path.Combine(root, keySetting));

        return new ReleaseManagerConfiguration(
            RepositoryCoordinates.Parse(document.Repo),
            document.Token,
            string.IsNullOrWhiteSpace(document.Branch) ? "main" : document.Branch,
            mirrors,
            keyPath);
    }

    public override string ToString() => $"{Repository} ({DefaultBranch})";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record ConfigurationFile(
        string Repo,
        string Token,
        IReadOnlyList<string>? Mirrors,
        string? SigningPrivateKeyPath,
        string? Branch);
}
