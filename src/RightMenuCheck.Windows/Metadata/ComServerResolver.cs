using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using RightMenuCheck.Core.Inventory;
using RightMenuCheck.Core.Metadata;
using RightMenuCheck.Windows.Registry;

namespace RightMenuCheck.Windows.Metadata;

public sealed class ComServerResolver
{
    private const string ClassesRoot = "Software\\Classes";
    private const int MaximumTreatAsDepth = 4;
    private readonly IRegistryReader _registryReader;

    public ComServerResolver(IRegistryReader registryReader)
    {
        _registryReader = registryReader ?? throw new ArgumentNullException(nameof(registryReader));
    }

    public IReadOnlyList<HandlerComponentMetadata> Resolve(ContextMenuRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        var references = GetComponentReferences(registration);
        var results = new List<HandlerComponentMetadata>(references.Count);

        foreach (var reference in references)
        {
            var issues = new List<MetadataIssue>();
            ComServerRegistration? comServer = null;
            try
            {
                comServer = registration.Source is PackageContextMenuSource packageSource
                    ? ResolvePackaged(reference.Clsid, packageSource, issues)
                    : ResolveClassic(reference.Clsid, registration, issues);
            }
            catch (ArgumentException exception)
            {
                AddPathIssue(exception);
            }
            catch (NotSupportedException exception)
            {
                AddPathIssue(exception);
            }
            catch (PathTooLongException exception)
            {
                AddPathIssue(exception);
            }

            results.Add(new HandlerComponentMetadata(
                reference.Role,
                reference.Clsid,
                comServer,
                Binary: null,
                issues));

            void AddPathIssue(Exception exception)
            {
                issues.Add(new MetadataIssue(
                    reference.Clsid,
                    "ResolveServerPath",
                    exception.GetType().Name,
                    exception.Message));
            }
        }

        return results;
    }

    private ComServerRegistration? ResolveClassic(
        string clsid,
        ContextMenuRegistration registration,
        List<MetadataIssue> issues)
    {
        if (registration.Source is not RegistryContextMenuSource registrySource)
        {
            return null;
        }

        var currentClsid = clsid;
        string? treatAsClsid = null;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentClsid };

        for (var depth = 0; depth <= MaximumTreatAsDepth; depth++)
        {
            foreach (var hive in new[]
                     {
                         RegistryHiveKind.CurrentUser,
                         RegistryHiveKind.LocalMachine,
                     })
            {
                var clsidPath = Combine(ClassesRoot, "CLSID", currentClsid);
                var displayName = ReadString(
                    hive,
                    registrySource.Location.View,
                    clsidPath,
                    valueName: null,
                    issues);
                var inProcessPath = Combine(clsidPath, "InprocServer32");
                var inProcessServer = ReadString(
                    hive,
                    registrySource.Location.View,
                    inProcessPath,
                    valueName: null,
                    issues);

                if (!string.IsNullOrWhiteSpace(inProcessServer))
                {
                    return new ComServerRegistration(
                        clsid,
                        ComServerKind.InProcess,
                        displayName,
                        inProcessServer,
                        ResolveInProcessPath(inProcessServer),
                        ReadString(
                            hive,
                            registrySource.Location.View,
                            inProcessPath,
                            "ThreadingModel",
                            issues),
                        treatAsClsid,
                        new RegistrySource(hive, registrySource.Location.View, inProcessPath),
                        PackageFullName: null,
                        PackageServerId: null);
                }

                var localServerPath = Combine(clsidPath, "LocalServer32");
                var localServer = ReadString(
                    hive,
                    registrySource.Location.View,
                    localServerPath,
                    valueName: null,
                    issues);
                if (!string.IsNullOrWhiteSpace(localServer))
                {
                    return new ComServerRegistration(
                        clsid,
                        ComServerKind.LocalServer,
                        displayName,
                        localServer,
                        ResolveLocalServerPath(localServer, clsid, issues),
                        ThreadingModel: null,
                        treatAsClsid,
                        new RegistrySource(hive, registrySource.Location.View, localServerPath),
                        PackageFullName: null,
                        PackageServerId: null);
                }
            }

            var nextClsid = ResolveTreatAs(currentClsid, registrySource.Location.View, issues);
            if (nextClsid is null)
            {
                break;
            }

            treatAsClsid ??= nextClsid;
            if (!visited.Add(nextClsid))
            {
                issues.Add(new MetadataIssue(
                    clsid,
                    "ResolveTreatAs",
                    "CycleDetected",
                    "TreatAs registration contains a cycle."));
                break;
            }

            currentClsid = nextClsid;
        }

        issues.Add(new MetadataIssue(
            clsid,
            "ResolveComServer",
            "RegistrationNotFound",
            "No classic COM server registration was found for this registry view."));
        return null;
    }

    private ComServerRegistration? ResolvePackaged(
        string clsid,
        PackageContextMenuSource packageSource,
        List<MetadataIssue> issues)
    {
        foreach (var view in GetPackageViews(packageSource.Architecture))
        {
            foreach (var hive in new[]
                     {
                         RegistryHiveKind.CurrentUser,
                         RegistryHiveKind.LocalMachine,
                     })
            {
                var classPath = Combine(
                    ClassesRoot,
                    "PackagedCom",
                    "Package",
                    packageSource.PackageFullName,
                    "Class",
                    clsid);
                var dllPath = ReadString(hive, view, classPath, "DllPath", issues) ??
                              ReadArchitectureSpecificDllPath(
                                  hive,
                                  view,
                                  classPath,
                                  packageSource.Architecture,
                                  issues);
                var serverId = ReadInteger(hive, view, classPath, "ServerId", issues);
                var registrySource = new RegistrySource(hive, view, classPath);

                if (!string.IsNullOrWhiteSpace(dllPath))
                {
                    return new ComServerRegistration(
                        clsid,
                        ComServerKind.PackagedInProcess,
                        DisplayName: null,
                        dllPath,
                        ResolvePackagePath(packageSource.ManifestPath, dllPath),
                        ReadString(hive, view, classPath, "Threading", issues),
                        TreatAsClsid: null,
                        registrySource,
                        packageSource.PackageFullName,
                        serverId);
                }

                if (serverId is null)
                {
                    continue;
                }

                var serverPath = Combine(
                    ClassesRoot,
                    "PackagedCom",
                    "Package",
                    packageSource.PackageFullName,
                    "Server",
                    serverId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var executable = ReadString(hive, view, serverPath, "Executable", issues);
                var surrogateAppId = ReadString(hive, view, serverPath, "SurrogateAppId", issues);
                var kind = executable is not null
                    ? ComServerKind.PackagedLocalServer
                    : ComServerKind.PackagedSurrogate;

                return new ComServerRegistration(
                    clsid,
                    kind,
                    ReadString(hive, view, serverPath, "DisplayName", issues),
                    executable,
                    executable is null
                        ? null
                        : ResolvePackagePath(packageSource.ManifestPath, executable),
                    ThreadingModel: null,
                    TreatAsClsid: surrogateAppId,
                    new RegistrySource(hive, view, serverPath),
                    packageSource.PackageFullName,
                    serverId);
            }
        }

        issues.Add(new MetadataIssue(
            clsid,
            "ResolvePackagedComServer",
            "RegistrationNotFound",
            "No Packaged COM class registration was found for this package."));
        return null;
    }

    private string? ResolveTreatAs(
        string clsid,
        RegistryViewKind view,
        List<MetadataIssue> issues)
    {
        foreach (var hive in new[]
                 {
                     RegistryHiveKind.CurrentUser,
                     RegistryHiveKind.LocalMachine,
                 })
        {
            var rawTreatAs = ReadString(
                hive,
                view,
                Combine(ClassesRoot, "CLSID", clsid, "TreatAs"),
                valueName: null,
                issues);
            if (ClsidUtilities.Normalize(rawTreatAs) is { } normalized)
            {
                return normalized;
            }
        }

        return null;
    }

    private string? ReadArchitectureSpecificDllPath(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string classPath,
        PackageArchitectureKind architecture,
        List<MetadataIssue> issues)
    {
        var valueNames = architecture switch
        {
            PackageArchitectureKind.X86 => new[] { "DllPath_x86" },
            PackageArchitectureKind.X64 => ["DllPath_x64"],
            PackageArchitectureKind.Arm => ["DllPath_arm"],
            PackageArchitectureKind.Arm64 => ["DllPath_arm64"],
            PackageArchitectureKind.X86OnArm64 => ["DllPath_x86"],
            _ => GetNativeDllPathValueNames(),
        };

        foreach (var valueName in valueNames)
        {
            if (ReadString(hive, view, classPath, valueName, issues) is { } dllPath)
            {
                return dllPath;
            }
        }

        return null;
    }

    private IReadOnlyList<RegistryViewKind> GetPackageViews(PackageArchitectureKind architecture) =>
        architecture switch
        {
            PackageArchitectureKind.X86 or PackageArchitectureKind.X86OnArm64 =>
                [RegistryViewKind.Registry32],
            PackageArchitectureKind.X64 or PackageArchitectureKind.Arm64 =>
                [RegistryViewKind.Registry64],
            _ => _registryReader.AvailableViews,
        };

    private string? ReadString(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string? valueName,
        List<MetadataIssue> issues)
    {
        var value = ReadValue(hive, view, keyPath, valueName, issues)?.Value;
        return value switch
        {
            null => null,
            string text => text,
            _ => value.ToString(),
        };
    }

    private int? ReadInteger(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string valueName,
        List<MetadataIssue> issues)
    {
        var value = ReadValue(hive, view, keyPath, valueName, issues)?.Value;
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            _ => null,
        };
    }

    private RegistryValueData? ReadValue(
        RegistryHiveKind hive,
        RegistryViewKind view,
        string keyPath,
        string? valueName,
        List<MetadataIssue> issues)
    {
        try
        {
            return _registryReader.GetValue(hive, view, keyPath, valueName);
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

        return null;

        void AddIssue(Exception exception)
        {
            issues.Add(new MetadataIssue(
                keyPath,
                "ReadRegistryValue",
                exception.GetType().Name,
                exception.Message));
        }
    }

    private static List<ComponentReference> GetComponentReferences(
        ContextMenuRegistration registration)
    {
        var references = new List<ComponentReference>(capacity: 3);
        if (registration.HandlerClsid is { } handlerClsid)
        {
            var role = registration.Kind switch
            {
                ContextMenuRegistrationKind.ClassicContextMenuHandler =>
                    HandlerComponentRole.ContextMenuHandler,
                _ => HandlerComponentRole.ExplorerCommand,
            };
            references.Add(new ComponentReference(role, handlerClsid));
        }

        if (registration.CommandStateHandlerClsid is { } commandStateHandlerClsid)
        {
            references.Add(new ComponentReference(
                HandlerComponentRole.CommandStateHandler,
                commandStateHandlerClsid));
        }

        if (registration.DelegateExecuteClsid is { } delegateExecuteClsid)
        {
            references.Add(new ComponentReference(
                HandlerComponentRole.DelegateExecute,
                delegateExecuteClsid));
        }

        return references;
    }

    private static string? ResolveInProcessPath(string rawPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        if (expanded.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0)
        {
            return Path.Combine(Environment.SystemDirectory, expanded);
        }

        return expanded;
    }

    private static string? ResolveLocalServerPath(
        string commandLine,
        string clsid,
        List<MetadataIssue> issues)
    {
        try
        {
            return CommandLineParser.TryGetExecutable(commandLine);
        }
        catch (Win32Exception exception)
        {
            issues.Add(new MetadataIssue(
                clsid,
                "ParseLocalServerCommandLine",
                exception.GetType().Name,
                exception.Message));
            return null;
        }
    }

    private static string ResolvePackagePath(string manifestPath, string relativeOrAbsolutePath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(relativeOrAbsolutePath.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        var packageRoot = Path.GetDirectoryName(manifestPath) ?? string.Empty;
        return Path.GetFullPath(Path.Combine(packageRoot, expanded));
    }

    private static string[] GetNativeDllPathValueNames() =>
        RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => ["DllPath_x86"],
            Architecture.X64 => ["DllPath_x64", "DllPath_x86"],
            Architecture.Arm => ["DllPath_arm"],
            Architecture.Arm64 => ["DllPath_arm64", "DllPath_x64", "DllPath_x86"],
            _ => ["DllPath_x64", "DllPath_x86", "DllPath_arm64", "DllPath_arm"],
        };

    private static string Combine(params string[] parts) =>
        string.Join('\\', parts.Where(static part => !string.IsNullOrWhiteSpace(part)))
            .Replace('/', '\\')
            .Trim('\\');

    private sealed record ComponentReference(HandlerComponentRole Role, string Clsid);
}
