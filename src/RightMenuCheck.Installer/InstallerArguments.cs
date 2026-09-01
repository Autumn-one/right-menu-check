namespace RightMenuCheck.Installer;

internal sealed record InstallerArguments(
    bool Silent,
    bool CreateDesktopShortcut,
    bool LaunchAfterInstall)
{
    public static InstallerArguments Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var silent = false;
        var desktopShortcut = true;
        var launch = false;
        foreach (var argument in arguments)
        {
            if (argument.Equals("--silent", StringComparison.Ordinal))
            {
                silent = true;
            }
            else if (argument.Equals("--no-desktop-shortcut", StringComparison.Ordinal))
            {
                desktopShortcut = false;
            }
            else if (argument.Equals("--launch", StringComparison.Ordinal))
            {
                launch = true;
            }
            else
            {
                throw new ArgumentException($"Unsupported setup argument: {argument}");
            }
        }

        if (!silent && arguments.Count > 0)
        {
            throw new ArgumentException("Setup options require --silent mode.");
        }

        return new InstallerArguments(silent, desktopShortcut, launch);
    }
}
