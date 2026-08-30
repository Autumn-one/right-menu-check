using System.Text.Json;
using System.Text.Json.Serialization;

namespace RightMenuCheck.Windows.Backup;

internal static class BackupJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
