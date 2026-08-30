using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public sealed record UpdateHealthReport(
    [property: JsonPropertyOrder(0)] string Token,
    [property: JsonPropertyOrder(1)] int ProcessId,
    [property: JsonPropertyOrder(2)] string Version);
