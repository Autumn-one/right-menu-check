using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public sealed record UpdateInstallRequest(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] int ParentProcessId,
    [property: JsonPropertyOrder(2)] string PackagePath,
    [property: JsonPropertyOrder(3)] string ParentApplicationPath,
    [property: JsonPropertyOrder(4)] string InstallDirectory,
    [property: JsonPropertyOrder(5)] SignedUpdateManifest Manifest,
    [property: JsonPropertyOrder(6)] string HealthToken,
    [property: JsonPropertyOrder(7)] string ReadyPipeName,
    [property: JsonPropertyOrder(8)] string ReadyNonce)
{
    public const int CurrentSchemaVersion = 1;
}
