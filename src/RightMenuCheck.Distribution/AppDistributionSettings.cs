using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public sealed record AppDistributionSettings(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string Repository,
    [property: JsonPropertyOrder(2)] string Branch,
    [property: JsonPropertyOrder(3)] string UpdateManifestPath,
    [property: JsonPropertyOrder(4)] string AnnouncementPath,
    [property: JsonPropertyOrder(5)] IReadOnlyList<string> MirrorPrefixes,
    [property: JsonPropertyOrder(6)] string? TelemetryBaseUrl)
{
    public const int CurrentSchemaVersion = 1;

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

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException("The distribution settings schema is unsupported.");
        }

        _ = GetUpdateManifestCandidates();
        _ = GetAnnouncementCandidates();
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
