using System.Reflection;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.App.Services;

public static class ApplicationVersionProvider
{
    public static SemanticVersion GetCurrent()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (SemanticVersion.TryParse(informational, out var semanticVersion))
        {
            return semanticVersion;
        }

        var version = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new SemanticVersion(version.Major, version.Minor, Math.Max(0, version.Build));
    }
}
