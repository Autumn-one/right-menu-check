using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public sealed record TelemetryStartRequest(
    [property: JsonPropertyOrder(0)] string MachineId,
    [property: JsonPropertyOrder(1)] string SessionId);

public sealed record TelemetryHeartbeatRequest(
    [property: JsonPropertyOrder(0)] string MachineId,
    [property: JsonPropertyOrder(1)] string SessionId);

public sealed record TelemetryEndRequest(
    [property: JsonPropertyOrder(0)] string MachineId,
    [property: JsonPropertyOrder(1)] string SessionId);

public sealed record TelemetryStartResponse(
    [property: JsonPropertyOrder(0)] int StartupCount,
    [property: JsonPropertyOrder(1)] DateTimeOffset StartedAtUtc);

public static class TelemetryIdentityValidator
{
    public static bool IsValidMachineId(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool IsValidSessionId(string? value) =>
        Guid.TryParseExact(value, "N", out _);
}
