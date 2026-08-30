namespace RightMenuCheck.Distribution;

public static class UpdateInstallLocations
{
    public const string ApplicationFileName = "RightMenuCheck.App.exe";

    public static string GetPerUserInstallDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "RightMenuCheck");
}
