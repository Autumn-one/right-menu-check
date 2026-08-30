namespace RightMenuCheck.Distribution;

public sealed record RepositoryCoordinates(string Owner, string Name)
{
    public static RepositoryCoordinates Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Trim().Split('/');
        if (parts.Length != 2 || parts.Any(static part =>
                part.Length == 0 ||
                part.Any(static character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')))
        {
            throw new FormatException("Repository coordinates must use the 'owner/name' format.");
        }

        return new RepositoryCoordinates(parts[0], parts[1]);
    }

    public override string ToString() => $"{Owner}/{Name}";
}

public static class DistributionEndpoints
{
    public static IReadOnlyList<string> DefaultMirrorPrefixes { get; } =
    [
        "https://ghfast.top/",
        "https://gh-proxy.com/",
    ];

    public static IReadOnlyList<string> BuildRawContentCandidates(
        RepositoryCoordinates repository,
        string branch,
        string relativePath,
        IReadOnlyList<string>? mirrorPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        var path = EscapePath(relativePath);
        var direct = $"https://raw.githubusercontent.com/{Escape(repository.Owner)}/" +
                     $"{Escape(repository.Name)}/{Escape(branch)}/{path}";
        return WithMirrors(direct, mirrorPrefixes);
    }

    public static IReadOnlyList<string> BuildReleaseDownloadCandidates(
        RepositoryCoordinates repository,
        string tag,
        string assetName,
        IReadOnlyList<string>? mirrorPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        if (!Path.GetFileName(assetName).Equals(assetName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Release asset name cannot contain a directory.", nameof(assetName));
        }

        var direct = $"https://github.com/{Escape(repository.Owner)}/{Escape(repository.Name)}/" +
                     $"releases/download/{Escape(tag)}/{Escape(assetName)}";
        return WithMirrors(direct, mirrorPrefixes);
    }

    private static string[] WithMirrors(
        string direct,
        IReadOnlyList<string>? mirrorPrefixes)
    {
        var prefixes = mirrorPrefixes ?? DefaultMirrorPrefixes;
        var candidates = new List<string>(prefixes.Count + 1);
        foreach (var prefix in prefixes)
        {
            if (!Uri.TryCreate(prefix, UriKind.Absolute, out var mirror) ||
                !mirror.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Mirror prefixes must use HTTPS.", nameof(mirrorPrefixes));
            }

            candidates.Add($"{prefix.TrimEnd('/')}/{direct}");
        }

        candidates.Add(direct);
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string EscapePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Replace('\\', '/').Split('/');
        if (parts.Any(static part => part.Length == 0 || part is "." or ".."))
        {
            throw new ArgumentException("Distribution paths must be relative and normalized.", nameof(value));
        }

        return string.Join('/', parts.Select(Escape));
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
