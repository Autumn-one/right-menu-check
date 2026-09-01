using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public sealed record AppDistributionSettings(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string Repository,
    [property: JsonPropertyOrder(2)] string Branch,
    [property: JsonPropertyOrder(3)] string UpdateManifestPath,
    [property: JsonPropertyOrder(4)] string AnnouncementPath,
    [property: JsonPropertyOrder(5)] IReadOnlyList<string> MirrorPrefixes,
    [property: JsonPropertyOrder(6)] string? TelemetryBaseUrl,
    [property: JsonPropertyOrder(7)] TelemetryDiscoverySettings? TelemetryDiscovery = null)
{
    public const int CurrentSchemaVersion = 2;

    public RepositoryCoordinates GetRepository() => RepositoryCoordinates.Parse(Repository);

    public IReadOnlyList<string> GetUpdateManifestCandidates() =>
        DistributionEndpoints.BuildRawContentCandidates(
            GetRepository(),
            Branch,
            UpdateManifestPath,
            MirrorPrefixes);

    public IReadOnlyList<string> GetAnnouncementCandidates() =>
        DistributionEndpoints.BuildRawContentCandidates(
            GetRepository(),
            Branch,
            AnnouncementPath,
            MirrorPrefixes);

    public IReadOnlyList<string> GetTelemetryEndpointCandidates() =>
        TelemetryDiscovery is null
            ? []
            : DistributionEndpoints.BuildRawContentCandidates(
                TelemetryDiscovery.GetRepository(),
                TelemetryDiscovery.Branch,
                TelemetryDiscovery.ConfigPath,
                MirrorPrefixes);

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("The distribution settings schema is unsupported.");
        }

        _ = GetUpdateManifestCandidates();
        _ = GetAnnouncementCandidates();
        if (TelemetryDiscovery is not null)
        {
            TelemetryDiscovery.Validate();
            _ = GetTelemetryEndpointCandidates();
        }

        if (string.IsNullOrWhiteSpace(TelemetryBaseUrl))
        {
            return;
        }

        if (!Uri.TryCreate(TelemetryBaseUrl, UriKind.Absolute, out var telemetryUri) ||
            !IsAllowedTelemetryUri(telemetryUri))
        {
            throw new InvalidDataException(
                "The telemetry URL must use HTTPS, except for loopback integration tests.");
        }
    }

    private static bool IsAllowedTelemetryUri(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback;
}

public sealed record TelemetryDiscoverySettings(
    [property: JsonPropertyOrder(0)] string Repository,
    [property: JsonPropertyOrder(1)] string Branch,
    [property: JsonPropertyOrder(2)] string ConfigPath,
    [property: JsonPropertyOrder(3)] string ProductId)
{
    public RepositoryCoordinates GetRepository() => RepositoryCoordinates.Parse(Repository);

    public void Validate()
    {
        _ = GetRepository();
        ArgumentException.ThrowIfNullOrWhiteSpace(Branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConfigPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProductId);
        if (ProductId.Length > 64 || ProductId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.'))
        {
            throw new InvalidDataException("The telemetry product identifier is invalid.");
        }
    }
}
