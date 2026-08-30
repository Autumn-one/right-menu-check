using System.IO;
using System.Reflection;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.App.Services;

public sealed record EmbeddedDistributionConfiguration(
    AppDistributionSettings Settings,
    string PublicKeyPem);

public static class EmbeddedDistributionConfigurationLoader
{
    private const string SettingsSuffix = ".distribution-settings.json";
    private const string PublicKeySuffix = ".update-public-key.pem";

    public static EmbeddedDistributionConfiguration Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var settings = DistributionJson.Deserialize<AppDistributionSettings>(
            ReadResource(assembly, SettingsSuffix));
        settings.Validate();
        return new EmbeddedDistributionConfiguration(
            settings,
            ReadResource(assembly, PublicKeySuffix));
    }

    private static string ReadResource(Assembly assembly, string suffix)
    {
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.EndsWith(suffix, StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidDataException($"Embedded distribution resource '{suffix}' is missing.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName) ??
                           throw new InvalidDataException(
                               $"Embedded distribution resource '{suffix}' cannot be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
