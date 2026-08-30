using RightMenuCheck.Distribution;

namespace RightMenuCheck.Updater;

public interface IUpdateTargetPolicy
{
    string ResolveTarget(string parentApplicationPath, string requestedInstallDirectory);
}

public sealed class SystemUpdateTargetPolicy : IUpdateTargetPolicy
{
    public string ResolveTarget(string parentApplicationPath, string requestedInstallDirectory)
    {
        var parentApplication = Path.GetFullPath(parentApplicationPath);
        var sourceDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetDirectoryName(parentApplication)!);
        var requested = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(requestedInstallDirectory));
        var perUserDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(UpdateInstallLocations.GetPerUserInstallDirectory()));
        if (!requested.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase) &&
            !requested.Equals(perUserDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update target is outside the allowed install locations.");
        }

        return requested;
    }
}
