using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public sealed record TelemetryStartRequest(
    [property: JsonPropertyOrder(0)] string MachineId,
    [property: JsonPropertyOrder(1)] string SessionId);

public sealed record TelemetryResumeRequest(
    [property: JsonPropertyOrder(0)] string MachineId,
    [property: JsonPropertyOrder(1)] string PreviousSessionId,
    [property: JsonPropertyOrder(2)] string SessionId);

public sealed record TelemetryHeartbeatRequest(
    [property: JsonPropertyOrder(0)] string MachineId,
    [property: JsonPropertyOrder(1)] string SessionId);

public sealed record TelemetryEndRequest(
    [property: JsonPropertyOrder(0)] string MachineId,
    [property: JsonPropertyOrder(1)] string SessionId);

public sealed record TelemetryStartResponse(
    [property: JsonPropertyOrder(0)] int StartupCount,
    [property: JsonPropertyOrder(1)] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyOrder(2)] string SessionToken);

public sealed record TelemetryErrorResponse(
    [property: JsonPropertyOrder(0)] string Code);

public static class TelemetryIdentityValidator
{
    public static bool IsValidMachineId(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool IsValidSessionId(string? value) =>
        Guid.TryParseExact(value, "N", out _);

    public static bool IsValidSessionToken(string? value)
    {
        if (value is not { Length: 43 })
        {
            return false;
        }

        if (value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(
            $"{value.Replace('-', '+').Replace('_', '/')}=",
            decoded,
            out var bytesWritten) &&
            bytesWritten == decoded.Length;
    }
}
