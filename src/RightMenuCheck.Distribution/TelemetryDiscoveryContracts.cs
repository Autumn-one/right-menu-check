using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public static class TelemetryProducts
{
    public const string RightMenuCheck = "rightmenucheck";
}

public sealed record TelemetryEndpointPayload(
    [property: JsonPropertyOrder(0)] long Sequence,
    [property: JsonPropertyOrder(1)] DateTimeOffset IssuedAtUtc,
    [property: JsonPropertyOrder(2)] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyOrder(3)] string ProductId,
    [property: JsonPropertyOrder(4)] string BaseUrl);

public sealed record SignedTelemetryEndpoint(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] TelemetryEndpointPayload Payload,
    [property: JsonPropertyOrder(2)] string SignatureAlgorithm,
    [property: JsonPropertyOrder(3)] string Signature)
{
    public const int CurrentSchemaVersion = 1;

    public static SignedTelemetryEndpoint Create(
        TelemetryEndpointPayload payload,
        string privateKeyPem) =>
        new(
            CurrentSchemaVersion,
            payload,
            DistributionSignature.Algorithm,
            DistributionSignature.Sign(payload, privateKeyPem));

    public bool HasValidSignature(string publicKeyPem) =>
        SchemaVersion == CurrentSchemaVersion &&
        SignatureAlgorithm.Equals(DistributionSignature.Algorithm, StringComparison.Ordinal) &&
        DistributionSignature.Verify(Payload, Signature, publicKeyPem);
}

public enum TelemetryEndpointDecisionKind
{
    Available,
    Stale,
    Invalid,
}

public sealed record TelemetryEndpointDecision(
    TelemetryEndpointDecisionKind Kind,
    Uri? BaseAddress,
    bool AllowsInsecureHttp,
    string Reason);

public static class TelemetryEndpointPolicy
{
    public static TelemetryEndpointDecision Evaluate(
        SignedTelemetryEndpoint document,
        string publicKeyPem,
        string expectedProductId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProductId);
        if (!document.HasValidSignature(publicKeyPem))
        {
            return Invalid("The telemetry endpoint signature is invalid.");
        }

        var payload = document.Payload;
        if (payload.Sequence <= 0 ||
            payload.ExpiresAtUtc <= payload.IssuedAtUtc ||
            payload.IssuedAtUtc > now.AddMinutes(5) ||
            !payload.ProductId.Equals(expectedProductId, StringComparison.Ordinal))
        {
            return Invalid("The telemetry endpoint identity or validity window is invalid.");
        }

        if (!TryParseBaseAddress(payload.BaseUrl, out var baseAddress))
        {
            return Invalid("The telemetry endpoint URL is invalid.");
        }

        if (payload.ExpiresAtUtc <= now)
        {
            return new TelemetryEndpointDecision(
                TelemetryEndpointDecisionKind.Stale,
                baseAddress,
                AllowsInsecureHttp(baseAddress),
                "The signed telemetry endpoint has expired.");
        }

        return new TelemetryEndpointDecision(
            TelemetryEndpointDecisionKind.Available,
            baseAddress,
            AllowsInsecureHttp(baseAddress),
            "A signed telemetry endpoint is available.");
    }

    private static bool TryParseBaseAddress(string value, out Uri baseAddress)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) &&
            parsed.AbsoluteUri.EndsWith('/') &&
            string.IsNullOrEmpty(parsed.UserInfo) &&
            string.IsNullOrEmpty(parsed.Query) &&
            string.IsNullOrEmpty(parsed.Fragment))
        {
            baseAddress = parsed;
            return true;
        }

        baseAddress = null!;
        return false;
    }

    private static bool AllowsInsecureHttp(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        !uri.IsLoopback;

    private static TelemetryEndpointDecision Invalid(string reason) =>
        new(
            TelemetryEndpointDecisionKind.Invalid,
            BaseAddress: null,
            AllowsInsecureHttp: false,
            reason);
}
