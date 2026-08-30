using System.Reflection;

namespace RightMenuCheck.Updater;

internal static class EmbeddedDistributionPublicKey
{
    private const string ResourceSuffix = ".update-public-key.pem";

    public static string Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidDataException("Updater distribution public key is missing.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName) ??
                           throw new InvalidDataException(
                               "Updater distribution public key cannot be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
