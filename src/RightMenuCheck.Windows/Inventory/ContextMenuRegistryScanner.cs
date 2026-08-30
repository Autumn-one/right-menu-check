using System.Diagnostics;
using System.Security;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Windows.Inventory;

public sealed class ContextMenuRegistryScanner
{
    private const string ClassesRoot = "Software\\Classes";
    private const string ShellExtensionsRoot =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Shell Extensions";
    private const int MaximumCascadeDepth = 8;

    private static readonly string[] WellKnownClassPaths =
    [
        "*",
        "AllFilesystemObjects",
        "Directory",
        "Directory\\Background",
        "Folder",
        "Drive",
        "DesktopBackground",
        "LibraryFolder",
        "LibraryFolder\\Background",
        "SystemFileAssociations\\*",
    ];

    private static readonly HashSet<string> SkippedClassRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActivatableClasses",
        "AppID",
        "Applications",
        "CLSID",
        "Interface",
        "MIME",
        "PackagedCom",
        "Protocols",
        "TypeLib",
        "WOW6432Node",
    };

    private readonly IRegistryReader _reader;

    public ContextMenuRegistryScanner(IRegistryReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public ContextMenuScanResult Scan(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var registrations = new List<ContextMenuRegistration>();
        var issues = new List<RegistryScanIssue>();
        var blockedClsids = LoadBlockedClsids(issues, cancellationToken);

        foreach (var view in _reader.AvailableViews)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var hive in Enum.GetValues<RegistryHiveKind>())
            {
                var classPaths = DiscoverClassPaths(hive, view, issues, cancellationToken);
                foreach (var classPath in classPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ScanClassicHandlers(
                        hive,
                        view,
                        classPath,
                        blockedClsids[view],
                        registrations,
                        issues);
                    ScanShellContainer(
                        hive,
                        view,
                        classPath,
                        Combine(ClassesRoot, classPath, "shell"),
                        parentId: null,
                        depth: 0,
                        blockedClsids[view],
                        registrations,
                        issues,
                        cancellationToken);
                }
            }
        }

        MarkCurrentUserOverrides(registrations);
        stopwatch.Stop();

        var ordered = registrations
            .OrderBy(static item => item.TargetKind)
            .ThenBy(static item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static item => GetRegistrySource(item).View)
            .ThenBy(static item => GetRegistrySource(item).Hive)
            .ThenBy(static item => item.RegistrationPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ContextMenuScanResult(startedAt, stopwatch.Elapsed, ordered, issues.ToArray());
    }

    private Dictionary<RegistryViewKind, HashSet<string>> LoadBlockedClsids(
        List<RegistryScanIssue> issues,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<RegistryViewKind, HashSet<string>>();

        foreach (var view in _reader.AvailableViews)
        {
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in Enum.GetValues<RegistryHiveKind>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Combine(ShellExtensionsRoot, "Blocked");
                foreach (var valueName in SafeGetValueNames(hive, view, path, issues))
                {
                    if (ClsidUtilities.Normalize(valueName) is { } clsid)
                    {
                        blocked.Add(clsid);
                    }
                }
            }

            result.Add(view, blocked);
        }

        return result;
    }

    private HashSet<string> DiscoverClassPaths(
        RegistryHiveKind hive,
        RegistryViewKind view,
        List<RegistryScanIssue> issues,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(WellKnownClassPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var className in SafeGetSubKeyNames(hive, view, ClassesRoot, issues))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SkippedClassRoots.Contains(className) ||
                className.Equals("SystemFileAssociations", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var classChildren = SafeGetSubKeyNames(
                hive,
                view,
                Combine(ClassesRoot, className),
                issues);

            if (ContainsMenuContainer(classChildren))
            {
                result.Add(className);
            }
        }

        var systemFileAssociationsPath = Combine(ClassesRoot, "SystemFileAssociations");
        foreach (var associationName in SafeGetSubKeyNames(
                     hive,
                     view,
                     systemFileAssociationsPath,
                     issues))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classPath = Combine("SystemFileAssociations", associationName);
            var classChildren = SafeGetSubKeyNames(
                hive,
                view,
                Combine(ClassesRoot, classPath),
                issues);

            if (ContainsMenuContainer(classChildren))
            {
                result.Add(classPath);
            }
        }

        return result;
    }

    private void ScanClassicHandlers(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string classPath,
        HashSet<string> blockedClsids,
        List<ContextMenuRegistration> registrations,
        List<RegistryScanIssue> issues)
    {
        var handlersPath = Combine(
            ClassesRoot,
            classPath,
            "shellex",
            "ContextMenuHandlers");

        foreach (var handlerName in SafeGetSubKeyNames(hive, view, handlersPath, issues))
        {
            var registrationPath = Combine(handlersPath, handlerName);
            var rawDefaultValue = ReadString(hive, view, registrationPath, valueName: null, issues);
            var handlerClsid = ClsidUtilities.Normalize(rawDefaultValue) ??
                               ClsidUtilities.Normalize(handlerName);
            var status = IsBlocked(blockedClsids, handlerClsid)
                ? ContextMenuRegistrationStatus.Blocked
                : ContextMenuRegistrationStatus.None;

            registrations.Add(new ContextMenuRegistration
            {
                Id = CreateId(hive, view, registrationPath),
                Source = new RegistryContextMenuSource(
                    new RegistrySource(hive, view, registrationPath)),
                ClassPath = classPath,
                RegistrationPath = registrationPath,
                CanonicalName = handlerName,
                DisplayName = handlerName,
                TargetKind = ContextMenuClassClassifier.Classify(classPath),
                Kind = ContextMenuRegistrationKind.ClassicContextMenuHandler,
                Status = status,
                HandlerClsid = handlerClsid,
                AppliesTo = ReadString(hive, view, registrationPath, "AppliesTo", issues),
            });
        }
    }

    private void ScanShellContainer(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string classPath,
        string shellContainerPath,
        string? parentId,
        int depth,
        HashSet<string> blockedClsids,
        List<ContextMenuRegistration> registrations,
        List<RegistryScanIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var verbName in SafeGetSubKeyNames(hive, view, shellContainerPath, issues))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var verbPath = Combine(shellContainerPath, verbName);
            var commandPath = Combine(verbPath, "command");
            var nestedShellPath = Combine(verbPath, "shell");
            var nestedVerbNames = SafeGetSubKeyNames(hive, view, nestedShellPath, issues);

            var explorerCommandClsid = ClsidUtilities.Normalize(
                ReadString(hive, view, verbPath, "ExplorerCommandHandler", issues));
            var delegateExecuteClsid = ClsidUtilities.Normalize(
                ReadString(hive, view, commandPath, "DelegateExecute", issues) ??
                ReadString(hive, view, verbPath, "DelegateExecute", issues));
            var commandStateHandlerClsid = ClsidUtilities.Normalize(
                ReadString(hive, view, verbPath, "CommandStateHandler", issues));
            var subCommands = ParseSubCommands(
                ReadString(hive, view, verbPath, "SubCommands", issues));
            var extendedSubCommandsKey = ReadString(
                hive,
                view,
                verbPath,
                "ExtendedSubCommandsKey",
                issues);

            var kind = GetVerbKind(
                explorerCommandClsid,
                delegateExecuteClsid,
                subCommands,
                extendedSubCommandsKey,
                nestedVerbNames.Count > 0);
            var status = GetVerbStatus(
                hive,
                view,
                verbPath,
                blockedClsids,
                explorerCommandClsid,
                delegateExecuteClsid,
                commandStateHandlerClsid,
                issues);
            var id = CreateId(hive, view, verbPath);
            var displayName = ReadString(hive, view, verbPath, "MUIVerb", issues) ??
                              ReadString(hive, view, verbPath, valueName: null, issues) ??
                              verbName;

            registrations.Add(new ContextMenuRegistration
            {
                Id = id,
                Source = new RegistryContextMenuSource(
                    new RegistrySource(hive, view, verbPath)),
                ClassPath = classPath,
                RegistrationPath = verbPath,
                CanonicalName = verbName,
                DisplayName = displayName,
                TargetKind = ContextMenuClassClassifier.Classify(classPath),
                Kind = kind,
                Status = status,
                ParentId = parentId,
                HandlerClsid = explorerCommandClsid,
                DelegateExecuteClsid = delegateExecuteClsid,
                CommandStateHandlerClsid = commandStateHandlerClsid,
                Command = ReadString(hive, view, commandPath, valueName: null, issues),
                Icon = ReadString(hive, view, verbPath, "Icon", issues),
                AppliesTo = ReadString(hive, view, verbPath, "AppliesTo", issues),
                ExtendedSubCommandsKey = extendedSubCommandsKey,
                SubCommands = subCommands,
            });

            if (nestedVerbNames.Count == 0)
            {
                continue;
            }

            if (depth >= MaximumCascadeDepth)
            {
                issues.Add(new RegistryScanIssue(
                    new RegistrySource(hive, view, nestedShellPath),
                    "EnumerateCascade",
                    "DepthLimit",
                    $"Nested shell depth exceeds {MaximumCascadeDepth}."));
                continue;
            }

            ScanShellContainer(
                hive,
                view,
                classPath,
                nestedShellPath,
                id,
                depth + 1,
                blockedClsids,
                registrations,
                issues,
                cancellationToken);
        }
    }

    private ContextMenuRegistrationStatus GetVerbStatus(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string verbPath,
        HashSet<string> blockedClsids,
        string? explorerCommandClsid,
        string? delegateExecuteClsid,
        string? commandStateHandlerClsid,
        List<RegistryScanIssue> issues)
    {
        var status = ContextMenuRegistrationStatus.None;

        if (ValueExists(hive, view, verbPath, "LegacyDisable", issues))
        {
            status |= ContextMenuRegistrationStatus.LegacyDisabled;
        }

        if (ValueExists(hive, view, verbPath, "ProgrammaticAccessOnly", issues))
        {
            status |= ContextMenuRegistrationStatus.ProgrammaticOnly;
        }

        if (ValueExists(hive, view, verbPath, "Extended", issues))
        {
            status |= ContextMenuRegistrationStatus.ExtendedOnly;
        }

        if (IsBlocked(blockedClsids, explorerCommandClsid) ||
            IsBlocked(blockedClsids, delegateExecuteClsid) ||
            IsBlocked(blockedClsids, commandStateHandlerClsid))
        {
            status |= ContextMenuRegistrationStatus.Blocked;
        }

        return status;
    }

    private static ContextMenuRegistrationKind GetVerbKind(
        string? explorerCommandClsid,
        string? delegateExecuteClsid,
        string[] subCommands,
        string? extendedSubCommandsKey,
        bool hasNestedVerbs)
    {
        if (explorerCommandClsid is not null)
        {
            return ContextMenuRegistrationKind.ExplorerCommand;
        }

        if (delegateExecuteClsid is not null)
        {
            return ContextMenuRegistrationKind.DelegateExecuteVerb;
        }

        if (subCommands.Length > 0 || extendedSubCommandsKey is not null || hasNestedVerbs)
        {
            return ContextMenuRegistrationKind.CascadingVerb;
        }

        return ContextMenuRegistrationKind.StaticVerb;
    }

    private static string[] ParseSubCommands(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void MarkCurrentUserOverrides(List<ContextMenuRegistration> registrations)
    {
        var currentUserPaths = registrations
            .Where(static item =>
                GetRegistrySource(item).Hive == RegistryHiveKind.CurrentUser)
            .Select(CreateOverrideKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < registrations.Count; index++)
        {
            var registration = registrations[index];
            if (GetRegistrySource(registration).Hive == RegistryHiveKind.LocalMachine &&
                currentUserPaths.Contains(CreateOverrideKey(registration)))
            {
                registrations[index] = registration with
                {
                    Status = registration.Status |
                             ContextMenuRegistrationStatus.CurrentUserOverridePresent,
                };
            }
        }
    }

    private static string CreateOverrideKey(ContextMenuRegistration registration) =>
        $"{GetRegistrySource(registration).View}|{registration.Kind}|{registration.RegistrationPath}";

    private static bool ContainsMenuContainer(IReadOnlyList<string> childNames) =>
        childNames.Contains("shell", StringComparer.OrdinalIgnoreCase) ||
        childNames.Contains("shellex", StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> SafeGetSubKeyNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        List<RegistryScanIssue> issues) =>
        RunRegistryRead(
            hive,
            view,
            keyPath,
            "GetSubKeyNames",
            () => _reader.GetSubKeyNames(hive, view, keyPath),
            Array.Empty<string>(),
            issues);

    private IReadOnlyList<string> SafeGetValueNames(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        List<RegistryScanIssue> issues) =>
        RunRegistryRead(
            hive,
            view,
            keyPath,
            "GetValueNames",
            () => _reader.GetValueNames(hive, view, keyPath),
            Array.Empty<string>(),
            issues);

    private string? ReadString(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string? valueName,
        List<RegistryScanIssue> issues)
    {
        var value = SafeGetValue(hive, view, keyPath, valueName, issues)?.Value;
        return value switch
        {
            null => null,
            string text => text,
            string[] textItems => string.Join(';', textItems),
            _ => value.ToString(),
        };
    }

    private bool ValueExists(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string valueName,
        List<RegistryScanIssue> issues) =>
        SafeGetValue(hive, view, keyPath, valueName, issues) is not null;

    private RegistryValueData? SafeGetValue(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string? valueName,
        List<RegistryScanIssue> issues) =>
        RunRegistryRead<RegistryValueData?>(
            hive,
            view,
            keyPath,
            "GetValue",
            () => _reader.GetValue(hive, view, keyPath, valueName),
            fallback: null,
            issues);

    private static T RunRegistryRead<T>(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string operation,
        Func<T> read,
        T fallback,
        List<RegistryScanIssue> issues)
    {
        try
        {
            return read();
        }
        catch (UnauthorizedAccessException exception)
        {
            AddIssue(exception);
        }
        catch (SecurityException exception)
        {
            AddIssue(exception);
        }
        catch (IOException exception)
        {
            AddIssue(exception);
        }

        return fallback;

        void AddIssue(Exception exception)
        {
            issues.Add(new RegistryScanIssue(
                new RegistrySource(hive, view, keyPath),
                operation,
                exception.GetType().Name,
                exception.Message));
        }
    }

    private static bool IsBlocked(HashSet<string> blockedClsids, string? clsid) =>
        clsid is not null && blockedClsids.Contains(clsid);

    private static string CreateId(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string registrationPath) =>
        $"{hive}|{view}|{registrationPath}";

    private static RegistrySource GetRegistrySource(ContextMenuRegistration registration) =>
        ((RegistryContextMenuSource)registration.Source).Location;

    private static string Combine(params string[] parts) =>
        string.Join('\\', parts.Where(static part => !string.IsNullOrWhiteSpace(part)))
            .Replace('/', '\\')
            .Trim('\\');
}
