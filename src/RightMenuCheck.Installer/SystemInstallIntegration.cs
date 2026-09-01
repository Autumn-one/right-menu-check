using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RightMenuCheck.Installation;

namespace RightMenuCheck.Installer;

internal sealed class SystemInstallIntegration : IInstallIntegration
{
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RightMenuCheck";

    public async Task EnsureApplicationStoppedAsync(
        string applicationPath,
        CancellationToken cancellationToken) =>
        await InstalledApplicationProcessGuard.EnsureStoppedAsync(
                applicationPath,
                TimeSpan.FromSeconds(15),
                cancellationToken)
            .ConfigureAwait(false);

    public IInstallIntegrationSnapshot Capture(InstallationPaths paths) =>
        new SystemInstallSnapshot(
            ShortcutSnapshot.Capture(paths.StartMenuShortcutPath),
            ShortcutSnapshot.Capture(paths.DesktopShortcutPath),
            RegistrySnapshot.Capture());

    public void Apply(
        InstallationPaths paths,
        string version,
        bool createDesktopShortcut,
        long estimatedSizeKilobytes)
    {
        CreateShortcut(paths.StartMenuShortcutPath, paths.ApplicationPath);
        if (createDesktopShortcut)
        {
            CreateShortcut(paths.DesktopShortcutPath, paths.ApplicationPath);
        }
        else
        {
            File.Delete(paths.DesktopShortcutPath);
        }

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true) ??
                        throw new IOException("无法创建当前用户的卸载注册信息。");
        key.SetValue("DisplayName", "RightMenuCheck", RegistryValueKind.String);
        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
        key.SetValue("Publisher", "Autumn-one", RegistryValueKind.String);
        key.SetValue("InstallLocation", paths.InstallDirectory, RegistryValueKind.String);
        key.SetValue("DisplayIcon", $"{paths.ApplicationPath},0", RegistryValueKind.String);
        key.SetValue("UninstallString", Quote(paths.UninstallerPath), RegistryValueKind.String);
        key.SetValue(
            "QuietUninstallString",
            $"{Quote(paths.UninstallerPath)} --quiet",
            RegistryValueKind.String);
        key.SetValue(
            "URLInfoAbout",
            "https://github.com/Autumn-one/right-menu-check",
            RegistryValueKind.String);
        key.SetValue(
            "EstimatedSize",
            (int)Math.Clamp(estimatedSizeKilobytes, 1, int.MaxValue),
            RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue(
            "InstallDate",
            DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            RegistryValueKind.String);
    }

    public void Restore(InstallationPaths paths, IInstallIntegrationSnapshot snapshot)
    {
        var systemSnapshot = snapshot as SystemInstallSnapshot ??
                             throw new ArgumentException(
                                 "Install integration snapshot type is invalid.",
                                 nameof(snapshot));
        systemSnapshot.StartMenuShortcut.Restore(paths.StartMenuShortcutPath);
        systemSnapshot.DesktopShortcut.Restore(paths.DesktopShortcutPath);
        systemSnapshot.Registry.Restore();
    }

    private static void CreateShortcut(string shortcutPath, string applicationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(shortcutPath)!,
            $"RightMenuCheck.{Guid.NewGuid():N}.tmp.lnk");
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!;
            shell = Activator.CreateInstance(shellType) ??
                    throw new InvalidOperationException("无法创建快捷方式服务。");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                shell,
                [temporaryPath],
                CultureInfo.InvariantCulture);
            var shortcutType = shortcut!.GetType();
            SetComProperty(shortcutType, shortcut, "TargetPath", applicationPath);
            SetComProperty(
                shortcutType,
                shortcut,
                "WorkingDirectory",
                Path.GetDirectoryName(applicationPath)!);
            SetComProperty(shortcutType, shortcut, "IconLocation", $"{applicationPath},0");
            SetComProperty(shortcutType, shortcut, "Description", "RightMenuCheck 右键菜单诊断工具");
            shortcutType.InvokeMember(
                "Save",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                shortcut,
                args: null,
                CultureInfo.InvariantCulture);
            File.Move(temporaryPath, shortcutPath, overwrite: true);
        }
        finally
        {
            SafeFileTree.TryDeleteFile(temporaryPath);
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                _ = Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void SetComProperty(Type type, object target, string name, object value) =>
        type.InvokeMember(
            name,
            System.Reflection.BindingFlags.SetProperty,
            binder: null,
            target,
            [value],
            CultureInfo.InvariantCulture);

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private sealed record SystemInstallSnapshot(
        ShortcutSnapshot StartMenuShortcut,
        ShortcutSnapshot DesktopShortcut,
        RegistrySnapshot Registry) : IInstallIntegrationSnapshot;

    private sealed record ShortcutSnapshot(bool Exists, byte[]? Content)
    {
        public static ShortcutSnapshot Capture(string path)
        {
            if (!File.Exists(path))
            {
                return new ShortcutSnapshot(false, null);
            }

            var file = new FileInfo(path);
            if (file.Length > 1024 * 1024)
            {
                throw new InvalidDataException("Existing shortcut is unexpectedly large.");
            }

            return new ShortcutSnapshot(true, File.ReadAllBytes(path));
        }

        public void Restore(string path)
        {
            if (!Exists)
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Content ?? []);
        }
    }

    private sealed record RegistrySnapshot(
        bool Exists,
        IReadOnlyList<RegistryValueSnapshot> Values)
    {
        public static RegistrySnapshot Capture()
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: false);
            if (key is null)
            {
                return new RegistrySnapshot(false, []);
            }

            var values = key.GetValueNames()
                .Select(name => new RegistryValueSnapshot(
                    name,
                    key.GetValue(
                        name,
                        defaultValue: null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames),
                    key.GetValueKind(name)))
                .ToArray();
            return new RegistrySnapshot(true, values);
        }

        public void Restore()
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
            if (!Exists)
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath, writable: true) ??
                            throw new IOException("无法恢复卸载注册信息。");
            foreach (var value in Values)
            {
                key.SetValue(value.Name, value.Value ?? string.Empty, value.Kind);
            }
        }
    }

    private sealed record RegistryValueSnapshot(
        string Name,
        object? Value,
        RegistryValueKind Kind);
}
