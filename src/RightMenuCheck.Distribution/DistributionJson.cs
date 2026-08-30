using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RightMenuCheck.Distribution;

public static class DistributionJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions(writeIndented: false);
    private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(writeIndented: true);

    public static byte[] SerializeCanonical<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string Serialize<T>(T value, bool writeIndented = false) =>
        JsonSerializer.Serialize(value, writeIndented ? IndentedOptions : Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ??
        throw new InvalidDataException("Distribution JSON did not contain an object.");

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.Default,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
