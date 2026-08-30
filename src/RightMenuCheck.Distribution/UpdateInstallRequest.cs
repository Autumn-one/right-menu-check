using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public sealed record UpdateInstallRequest(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] int ParentProcessId,
    [property: JsonPropertyOrder(2)] string PackagePath,
    [property: JsonPropertyOrder(3)] string InstallDirectory,
    [property: JsonPropertyOrder(4)] string ApplicationFileName,
    [property: JsonPropertyOrder(5)] SignedUpdateManifest Manifest,
    [property: JsonPropertyOrder(6)] string HealthToken)
{
    public const int CurrentSchemaVersion = 1;
}
