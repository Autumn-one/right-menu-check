using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public sealed record UpdatePackage(
    [property: JsonPropertyOrder(0)] string AssetName,
    [property: JsonPropertyOrder(1)] long SizeBytes,
    [property: JsonPropertyOrder(2)] string Sha256,
    [property: JsonPropertyOrder(3)] string PrimaryUrl,
    [property: JsonPropertyOrder(4)] IReadOnlyList<string> MirrorUrls);

public sealed record UpdateManifestPayload(
    [property: JsonPropertyOrder(0)] long Sequence,
    [property: JsonPropertyOrder(1)] DateTimeOffset IssuedAtUtc,
    [property: JsonPropertyOrder(2)] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyOrder(3)] string Version,
    [property: JsonPropertyOrder(4)] UpdatePackage Package,
    [property: JsonPropertyOrder(5)] string ReleaseNotes,
    [property: JsonPropertyOrder(6)] string ReleasePageUrl);

public sealed record SignedUpdateManifest(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] UpdateManifestPayload Payload,
    [property: JsonPropertyOrder(2)] string SignatureAlgorithm,
    [property: JsonPropertyOrder(3)] string Signature)
{
    public const int CurrentSchemaVersion = 2;

    public static SignedUpdateManifest Create(
        UpdateManifestPayload payload,
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

public enum UpdateDecisionKind
{
    Current,
    Required,
    StaleManifest,
    InvalidManifest,
}

public sealed record UpdateDecision(
    UpdateDecisionKind Kind,
    SemanticVersion? TargetVersion,
    string Reason);

public static class UpdatePolicyEvaluator
{
    public static UpdateDecision Evaluate(
        SemanticVersion currentVersion,
        SignedUpdateManifest manifest,
        string publicKeyPem,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.HasValidSignature(publicKeyPem))
        {
            return Invalid("The update manifest signature is invalid.");
        }

        if (!SemanticVersion.TryParse(manifest.Payload.Version, out var targetVersion))
        {
            return Invalid("The update version is invalid.");
        }

        var packageError = ValidatePackage(manifest.Payload.Package);
        if (packageError is not null)
        {
            return Invalid(packageError);
        }

        if (manifest.Payload.Sequence <= 0 ||
            manifest.Payload.ExpiresAtUtc <= manifest.Payload.IssuedAtUtc ||
            manifest.Payload.IssuedAtUtc > now.AddMinutes(5))
        {
            return Invalid("The update manifest sequence or validity window is invalid.");
        }

        if (manifest.Payload.ExpiresAtUtc <= now)
        {
            return new UpdateDecision(
                UpdateDecisionKind.StaleManifest,
                targetVersion,
                "The signed update manifest has expired.");
        }

        if (targetVersion > currentVersion)
        {
            return new UpdateDecision(
                UpdateDecisionKind.Required,
                targetVersion,
                "A newer signed release must be installed before continuing.");
        }

        return new UpdateDecision(
            UpdateDecisionKind.Current,
            targetVersion,
            "The installed version is current.");
    }

    private static UpdateDecision Invalid(string reason) =>
        new(UpdateDecisionKind.InvalidManifest, TargetVersion: null, reason);

    private static string? ValidatePackage(UpdatePackage package)
    {
        if (string.IsNullOrWhiteSpace(package.AssetName) ||
            package.AssetName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return "The update asset name is invalid.";
        }

        if (package.SizeBytes <= 0 ||
            package.Sha256.Length != 64 ||
            !package.Sha256.All(Uri.IsHexDigit))
        {
            return "The update package integrity metadata is invalid.";
        }

        if (!IsHttps(package.PrimaryUrl) || package.MirrorUrls.Any(url => !IsHttps(url)))
        {
            return "Update package URLs must use HTTPS.";
        }

        return null;
    }

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
