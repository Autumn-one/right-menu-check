using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RightMenuCheck.Probe.Protocol;

namespace RightMenuCheck.Probe.Worker;

internal static class ShellProbeExecutor
{
    private const uint ClassContext =
        ShellInterop.ClassContextInProcessServer | ShellInterop.ClassContextLocalServer;
    private const uint FirstCommandId = 1;
    private const uint LastCommandId = 0x7FFF;
    private const int MaximumMenuDepth = 8;
    private const int MaximumMenuItems = 256;
    private const int MaximumMenuTextCharacters = 1024;

    public static ProbeResponse Execute(ProbeRequest request)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var totalStopwatch = Stopwatch.StartNew();

        try
        {
            return request.Operation switch
            {
                ProbeOperation.ClassicContextMenu => ExecuteClassic(
                    request,
                    startedAt,
                    totalStopwatch),
                ProbeOperation.ExplorerCommand => ExecuteExplorerCommand(
                    request,
                    startedAt,
                    totalStopwatch),
                ProbeOperation.AggregatedContextMenu => ExecuteAggregate(
                    request,
                    startedAt,
                    totalStopwatch),
                _ => CreateFailure(
                    request,
                    startedAt,
                    totalStopwatch.Elapsed,
                    ProbeOutcome.InvalidRequest,
                    [],
                    "UnsupportedOperation",
                    "The requested probe operation is not supported.",
                    hResult: null),
            };
        }
        catch (COMException exception)
        {
            return CreateFailureFromException(
                request,
                startedAt,
                totalStopwatch.Elapsed,
                ProbeOutcome.InitializationFailed,
                [],
                exception);
        }
        catch (Win32Exception exception)
        {
            return CreateFailureFromException(
                request,
                startedAt,
                totalStopwatch.Elapsed,
                ProbeOutcome.InitializationFailed,
                [],
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return CreateFailureFromException(
                request,
                startedAt,
                totalStopwatch.Elapsed,
                ProbeOutcome.InitializationFailed,
                [],
                exception);
        }
        catch (IOException exception)
        {
            return CreateFailureFromException(
                request,
                startedAt,
                totalStopwatch.Elapsed,
                ProbeOutcome.InitializationFailed,
                [],
                exception);
        }
        catch (InvalidOperationException exception)
        {
            return CreateFailureFromException(
                request,
                startedAt,
                totalStopwatch.Elapsed,
                ProbeOutcome.InitializationFailed,
                [],
                exception);
        }
        catch (TypeLoadException exception)
        {
            return CreateFailureFromException(
                request,
                startedAt,
                totalStopwatch.Elapsed,
                ProbeOutcome.InitializationFailed,
                [],
                exception);
        }
    }

    private static ProbeResponse ExecuteClassic(
        ProbeRequest request,
        DateTimeOffset startedAt,
        Stopwatch totalStopwatch)
    {
        using var targetContext = ShellTargetContext.CreateClassic(request);
        var phases = new List<ProbePhaseTiming>();
        ProbeMenuSnapshot? menuSnapshot = null;
        IContextMenu? contextMenu = null;

        try
        {
            var activation = Measure(() => Activate(request.HandlerClsid, out contextMenu));
            phases.Add(CreatePhase(ProbePhase.ComActivation, activation));
            if (activation.HResult < 0 || contextMenu is null)
            {
                return CreateHResultFailure(
                    request,
                    startedAt,
                    totalStopwatch.Elapsed,
                    ProbeOutcome.ActivationFailed,
                    phases,
                    "CoCreateInstance",
                    activation.HResult);
            }

            if (contextMenu is not IShellExtInit shellInitializer)
            {
                return CreateHResultFailure(
                    request,
                    startedAt,
                    totalStopwatch.Elapsed,
                    ProbeOutcome.InitializationFailed,
                    phases,
                    "QueryInterface:IShellExtInit",
                    ShellInterop.NoInterface);
            }

            var initialization = Measure(() => shellInitializer.Initialize(
                targetContext.FolderItemIdList,
                targetContext.DataObject,
                programIdKey: IntPtr.Zero));
            phases.Add(CreatePhase(ProbePhase.ShellInitialization, initialization));
            if (initialization.HResult < 0)
            {
                return CreateHResultFailure(
                    request,
                    startedAt,
                    totalStopwatch.Elapsed,
                    ProbeOutcome.InitializationFailed,
                    phases,
                    "IShellExtInit.Initialize",
                    initialization.HResult);
            }

            var menu = ShellInterop.CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                var error = Marshal.GetHRForLastWin32Error();
                return CreateHResultFailure(
                    request,
                    startedAt,
                    totalStopwatch.Elapsed,
                    ProbeOutcome.QueryFailed,
                    phases,
                    "CreatePopupMenu",
                    error);
            }

            try
            {
                var construction = Measure(() => contextMenu.QueryContextMenu(
                    menu,
                    indexMenu: 0,
                    firstCommandId: FirstCommandId,
                    lastCommandId: LastCommandId,
                    flags: 0));
                phases.Add(CreatePhase(ProbePhase.MenuConstruction, construction));
                if (construction.HResult < 0)
                {
                    return CreateHResultFailure(
                        request,
                        startedAt,
                        totalStopwatch.Elapsed,
                        ProbeOutcome.QueryFailed,
                        phases,
                        "IContextMenu.QueryContextMenu",
                        construction.HResult);
                }

                menuSnapshot = CaptureClassicMenu(contextMenu, menu, construction.HResult);
            }
            finally
            {
                _ = ShellInterop.DestroyMenu(menu);
            }

            totalStopwatch.Stop();
            return CreateSuccess(
                request,
                startedAt,
                totalStopwatch.Elapsed,
                phases,
                menuSnapshot);
        }
        finally
        {
            ShellTargetContext.ReleaseComObject(contextMenu);
        }
    }

    private static ProbeResponse ExecuteExplorerCommand(
        ProbeRequest request,
        DateTimeOffset startedAt,
        Stopwatch totalStopwatch)
    {
        using var targetContext = ShellTargetContext.CreateExplorerCommand(request);
        var phases = new List<ProbePhaseTiming>();
        IExplorerCommand? command = null;

        try
        {
            var activation = Measure(() => Activate(request.HandlerClsid, out command));
            phases.Add(CreatePhase(ProbePhase.ComActivation, activation));
            if (activation.HResult < 0 || command is null)
            {
                return CreateHResultFailure(
                    request,
                    startedAt,
                    totalStopwatch.Elapsed,
                    ProbeOutcome.ActivationFailed,
                    phases,
                    "CoCreateInstance",
                    activation.HResult);
            }

            string? commandTitle = null;
            var titlePointer = IntPtr.Zero;
            var title = Measure(() => command.GetTitle(targetContext.ItemArray, out titlePointer));
            try
            {
                phases.Add(CreatePhase(ProbePhase.GetTitle, title));
                if (title.HResult >= 0 && titlePointer != IntPtr.Zero)
                {
                    commandTitle = NormalizeMenuText(Marshal.PtrToStringUni(titlePointer));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(titlePointer);
            }

            if (title.HResult < 0)
            {
                return CreateHResultFailure(
                    request,
                    startedAt,
                    totalStopwatch.Elapsed,
                    ProbeOutcome.QueryFailed,
                    phases,
                    "IExplorerCommand.GetTitle",
                    title.HResult);
            }

            var iconPointer = IntPtr.Zero;
            var icon = Measure(() => command.GetIcon(targetContext.ItemArray, out iconPointer));
            try
            {
                phases.Add(CreatePhase(ProbePhase.GetIcon, icon));
            }
            finally
            {
                Marshal.FreeCoTaskMem(iconPointer);
            }

            uint commandState = 0;
            var state = Measure(() => command.GetState(
                targetContext.ItemArray,
                allowSlowOperations: false,
                out commandState));
            phases.Add(CreatePhase(ProbePhase.GetState, state));
            if (state.HResult < 0)
            {
                return CreateHResultFailure(
                    request,
                    startedAt,
                    totalStopwatch.Elapsed,
                    ProbeOutcome.QueryFailed,
                    phases,
                    "IExplorerCommand.GetState",
                    state.HResult);
            }

            var capture = new MenuCaptureState();
            capture.Items.Add(new ProbeMenuItem(
                ProbeMenuItemKind.Command,
                commandTitle,
                Depth: 0,
                CommandId: null,
                CanonicalVerb: ReadExplorerCanonicalVerb(command),
                HelpText: ReadExplorerTooltip(command, targetContext.ItemArray),
                IsDisabled: (commandState & 0x1) != 0,
                IsHidden: (commandState & 0x2) != 0));
            var enumeration = Measure(() => EnumerateSubCommands(
                command,
                targetContext.ItemArray,
                capture,
                depth: 1));
            phases.Add(CreatePhase(
                ProbePhase.EnumerateSubCommands,
                enumeration,
                optionalNotImplemented: true));
            if (capture.Items.Count > 1)
            {
                capture.Items[0] = capture.Items[0] with { Kind = ProbeMenuItemKind.Submenu };
            }

            var menuSnapshot = new ProbeMenuSnapshot(
                capture.Items.Count,
                capture.Items.ToArray(),
                capture.Truncated,
                capture.Limitation);

            totalStopwatch.Stop();
            return CreateSuccess(
                request,
                startedAt,
                totalStopwatch.Elapsed,
                phases,
                menuSnapshot);
        }
        finally
        {
            ShellTargetContext.ReleaseComObject(command);
        }
    }

    private static ProbeResponse ExecuteAggregate(
        ProbeRequest request,
        DateTimeOffset startedAt,
        Stopwatch totalStopwatch)
    {
        using var target = DefaultContextMenuTarget.Create(request);
        var phases = new List<ProbePhaseTiming>();
        ProbeMenuSnapshot? menuSnapshot = null;
        IContextMenu? contextMenu = null;

        try
        {
            var creation = Measure(() => CreateDefaultContextMenu(target, out contextMenu));
            phases.Add(CreatePhase(ProbePhase.AggregateMenuCreation, creation));
            if (creation.HResult < 0 || contextMenu is null)
            {
                return CreateHResultFailure(
                    request,
                    startedAt,
                    totalStopwatch.Elapsed,
                    ProbeOutcome.InitializationFailed,
                    phases,
                    "SHCreateDefaultContextMenu",
                    creation.HResult);
            }

            var menu = ShellInterop.CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return CreateHResultFailure(
                    request,
                    startedAt,
                    totalStopwatch.Elapsed,
                    ProbeOutcome.QueryFailed,
                    phases,
                    "CreatePopupMenu",
                    Marshal.GetHRForLastWin32Error());
            }

            try
            {
                var construction = Measure(() => contextMenu.QueryContextMenu(
                    menu,
                    indexMenu: 0,
                    firstCommandId: FirstCommandId,
                    lastCommandId: LastCommandId,
                    flags: 0));
                phases.Add(CreatePhase(ProbePhase.MenuConstruction, construction));
                if (construction.HResult < 0)
                {
                    return CreateHResultFailure(
                        request,
                        startedAt,
                        totalStopwatch.Elapsed,
                        ProbeOutcome.QueryFailed,
                        phases,
                        "IContextMenu.QueryContextMenu",
                        construction.HResult);
                }

                menuSnapshot = CaptureClassicMenu(contextMenu, menu, construction.HResult);
            }
            finally
            {
                _ = ShellInterop.DestroyMenu(menu);
            }

            totalStopwatch.Stop();
            return CreateSuccess(
                request,
                startedAt,
                totalStopwatch.Elapsed,
                phases,
                menuSnapshot);
        }
        finally
        {
            ShellTargetContext.ReleaseComObject(contextMenu);
        }
    }

    private static int CreateDefaultContextMenu(
        DefaultContextMenuTarget target,
        out IContextMenu? contextMenu)
    {
        contextMenu = null;
        var definition = new ShellInterop.DefaultContextMenu
        {
            FolderItemIdList = target.FolderItemIdList,
            ChildCount = target.ChildCount,
            ChildItemIdLists = target.ChildItemIdLists,
        };
        var interfaceId = typeof(IContextMenu).GUID;
        var result = ShellInterop.SHCreateDefaultContextMenu(
            ref definition,
            ref interfaceId,
            out var contextMenuPointer);
        if (result < 0)
        {
            return result;
        }

        try
        {
            contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPointer);
            return 0;
        }
        finally
        {
            _ = Marshal.Release(contextMenuPointer);
        }
    }

    private static ProbeMenuSnapshot CaptureClassicMenu(
        IContextMenu contextMenu,
        IntPtr menu,
        int queryResult)
    {
        var capture = new MenuCaptureState();
        EnumerateNativeMenu(contextMenu, menu, depth: 0, capture);
        return new ProbeMenuSnapshot(
            queryResult & 0xFFFF,
            capture.Items.ToArray(),
            capture.Truncated,
            capture.Limitation);
    }

    private static void EnumerateNativeMenu(
        IContextMenu contextMenu,
        IntPtr menu,
        int depth,
        MenuCaptureState capture)
    {
        if (depth > MaximumMenuDepth)
        {
            capture.MarkPartial($"Menu depth exceeded {MaximumMenuDepth}.");
            return;
        }

        var count = ShellInterop.GetMenuItemCount(menu);
        if (count < 0)
        {
            capture.MarkPartial(
                $"GetMenuItemCount failed at depth {depth} with Win32 error " +
                $"{Marshal.GetLastWin32Error()}.");
            return;
        }

        for (var position = 0; position < count; position++)
        {
            if (capture.Items.Count >= MaximumMenuItems)
            {
                capture.MarkPartial($"Menu item count exceeded {MaximumMenuItems}.");
                return;
            }

            var textBuffer = Marshal.AllocHGlobal(MaximumMenuTextCharacters * sizeof(char));
            try
            {
                var info = new ShellInterop.MenuItemInfo
                {
                    Size = (uint)Marshal.SizeOf<ShellInterop.MenuItemInfo>(),
                    Mask = ShellInterop.MenuItemMaskState |
                           ShellInterop.MenuItemMaskId |
                           ShellInterop.MenuItemMaskSubmenu |
                           ShellInterop.MenuItemMaskType,
                };
                if (!ShellInterop.GetMenuItemInfo(
                        menu,
                        (uint)position,
                        byPosition: true,
                        ref info))
                {
                    capture.MarkPartial(
                        $"GetMenuItemInfo failed at depth {depth}, position {position}, " +
                        $"with Win32 error {Marshal.GetLastWin32Error()}.");
                    continue;
                }

                var isSeparator = (info.Type & ShellInterop.MenuItemTypeSeparator) != 0;
                var isOwnerDrawn = (info.Type & ShellInterop.MenuItemTypeOwnerDrawn) != 0;
                var hasSubmenu = info.Submenu != IntPtr.Zero;
                var kind = isSeparator
                    ? ProbeMenuItemKind.Separator
                    : hasSubmenu
                        ? ProbeMenuItemKind.Submenu
                        : isOwnerDrawn
                            ? ProbeMenuItemKind.OwnerDrawn
                            : ProbeMenuItemKind.Command;
                string? title = null;
                if (!isSeparator && !isOwnerDrawn)
                {
                    Marshal.WriteInt16(textBuffer, 0);
                    info.Mask = ShellInterop.MenuItemMaskString;
                    info.TypeData = textBuffer;
                    info.CharacterCount = MaximumMenuTextCharacters;
                    if (ShellInterop.GetMenuItemInfo(
                            menu,
                            (uint)position,
                            byPosition: true,
                            ref info))
                    {
                        title = NormalizeMenuText(Marshal.PtrToStringUni(textBuffer));
                    }
                }
                uint? commandId = !isSeparator && info.Id is >= FirstCommandId and <= LastCommandId
                    ? info.Id
                    : null;
                var commandOffset = commandId is null ? null : commandId - FirstCommandId;
                capture.Items.Add(new ProbeMenuItem(
                    kind,
                    title,
                    depth,
                    commandId,
                    commandOffset is null
                        ? null
                        : ReadContextMenuString(
                            contextMenu,
                            commandOffset.Value,
                            ShellInterop.GetCommandStringVerbUnicode),
                    commandOffset is null
                        ? null
                        : ReadContextMenuString(
                            contextMenu,
                            commandOffset.Value,
                            ShellInterop.GetCommandStringHelpUnicode),
                    IsDisabled: (info.State & ShellInterop.MenuItemStateDisabled) != 0,
                    IsHidden: false));

                if (hasSubmenu)
                {
                    EnumerateNativeMenu(contextMenu, info.Submenu, depth + 1, capture);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(textBuffer);
            }
        }
    }

    private static string? ReadContextMenuString(
        IContextMenu contextMenu,
        uint commandOffset,
        uint stringType)
    {
        var buffer = Marshal.AllocHGlobal(MaximumMenuTextCharacters * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var result = contextMenu.GetCommandString(
                (UIntPtr)commandOffset,
                stringType,
                reserved: IntPtr.Zero,
                buffer,
                MaximumMenuTextCharacters);
            return result < 0 ? null : NormalizeMenuText(Marshal.PtrToStringUni(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int EnumerateSubCommands(
        IExplorerCommand command,
        IShellItemArray? itemArray,
        MenuCaptureState capture,
        int depth)
    {
        if (depth > MaximumMenuDepth)
        {
            capture.MarkPartial($"Explorer command depth exceeded {MaximumMenuDepth}.");
            return 0;
        }

        var result = command.EnumSubCommands(out var enumerator);
        if (result < 0 || enumerator is null)
        {
            return result;
        }

        try
        {
            while (capture.Items.Count < MaximumMenuItems)
            {
                var nextResult = enumerator.Next(1, out var childCommand, out var fetched);
                try
                {
                    if (nextResult == ShellInterop.FalseResult || fetched == 0)
                    {
                        return 0;
                    }

                    if (nextResult < 0)
                    {
                        return nextResult;
                    }

                    if (childCommand is null)
                    {
                        continue;
                    }

                    uint childState = 0;
                    _ = childCommand.GetState(
                        itemArray,
                        allowSlowOperations: false,
                        out childState);
                    var itemIndex = capture.Items.Count;
                    capture.Items.Add(new ProbeMenuItem(
                        ProbeMenuItemKind.Command,
                        ReadExplorerTitle(childCommand, itemArray),
                        depth,
                        CommandId: null,
                        CanonicalVerb: ReadExplorerCanonicalVerb(childCommand),
                        HelpText: ReadExplorerTooltip(childCommand, itemArray),
                        IsDisabled: (childState & 0x1) != 0,
                        IsHidden: (childState & 0x2) != 0));
                    var childResult = EnumerateSubCommands(
                        childCommand,
                        itemArray,
                        capture,
                        depth + 1);
                    if (capture.Items.Count > itemIndex + 1)
                    {
                        capture.Items[itemIndex] = capture.Items[itemIndex] with
                        {
                            Kind = ProbeMenuItemKind.Submenu,
                        };
                    }

                    if (childResult < 0 && childResult != ShellInterop.NotImplemented)
                    {
                        capture.MarkPartial(
                            $"IExplorerCommand.EnumSubCommands failed with HRESULT " +
                            $"0x{unchecked((uint)childResult):X8}.");
                        return childResult;
                    }
                }
                finally
                {
                    ShellTargetContext.ReleaseComObject(childCommand);
                }
            }

            capture.MarkPartial($"Explorer command count exceeded {MaximumMenuItems}.");
            return 0;
        }
        finally
        {
            ShellTargetContext.ReleaseComObject(enumerator);
        }
    }

    private static string? ReadExplorerTitle(
        IExplorerCommand command,
        IShellItemArray? itemArray)
    {
        var pointer = IntPtr.Zero;
        try
        {
            return command.GetTitle(itemArray, out pointer) < 0 || pointer == IntPtr.Zero
                ? null
                : NormalizeMenuText(Marshal.PtrToStringUni(pointer));
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static string? ReadExplorerTooltip(
        IExplorerCommand command,
        IShellItemArray? itemArray)
    {
        var pointer = IntPtr.Zero;
        try
        {
            return command.GetToolTip(itemArray, out pointer) < 0 || pointer == IntPtr.Zero
                ? null
                : NormalizeMenuText(Marshal.PtrToStringUni(pointer));
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static string? ReadExplorerCanonicalVerb(IExplorerCommand command) =>
        command.GetCanonicalName(out var canonicalName) < 0 || canonicalName == Guid.Empty
            ? null
            : canonicalName.ToString("B").ToUpperInvariant();

    private static string? NormalizeMenuText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int Activate<T>(string rawClsid, out T? instance)
        where T : class
    {
        instance = null;
        var classId = Guid.Parse(rawClsid);
        var interfaceId = typeof(T).GUID;
        var result = ShellInterop.CoCreateInstance(
            ref classId,
            outer: IntPtr.Zero,
            ClassContext,
            ref interfaceId,
            out var instancePointer);
        if (result < 0)
        {
            return result;
        }

        try
        {
            instance = (T)Marshal.GetObjectForIUnknown(instancePointer);
            return 0;
        }
        finally
        {
            _ = Marshal.Release(instancePointer);
        }
    }

    private static TimedHResult Measure(Func<int> operation)
    {
        var start = Stopwatch.GetTimestamp();
        var result = operation();
        return new TimedHResult(result, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private static ProbePhaseTiming CreatePhase(
        ProbePhase phase,
        TimedHResult result,
        bool optionalNotImplemented = false) =>
        new(
            phase,
            result.DurationMilliseconds,
            result.HResult,
            result.HResult >= 0 ||
            (optionalNotImplemented && result.HResult == ShellInterop.NotImplemented));

    private static ProbeResponse CreateSuccess(
        ProbeRequest request,
        DateTimeOffset startedAt,
        TimeSpan duration,
        IReadOnlyList<ProbePhaseTiming> phases,
        ProbeMenuSnapshot? menu) =>
        new(
            ProbeProtocol.CurrentVersion,
            request.RequestId,
            request.Nonce,
            ProbeOutcome.Success,
            Environment.ProcessId,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            startedAt,
            duration.TotalMilliseconds,
            phases,
            menu,
            Error: null);

    private static ProbeResponse CreateHResultFailure(
        ProbeRequest request,
        DateTimeOffset startedAt,
        TimeSpan duration,
        ProbeOutcome outcome,
        IReadOnlyList<ProbePhaseTiming> phases,
        string operation,
        int hResult)
    {
        var message = Marshal.GetExceptionForHR(hResult)?.Message ??
                      $"The operation returned HRESULT 0x{unchecked((uint)hResult):X8}.";
        return CreateFailure(
            request,
            startedAt,
            duration,
            outcome,
            phases,
            operation,
            message,
            hResult);
    }

    private static ProbeResponse CreateFailureFromException(
        ProbeRequest request,
        DateTimeOffset startedAt,
        TimeSpan duration,
        ProbeOutcome outcome,
        IReadOnlyList<ProbePhaseTiming> phases,
        Exception exception) =>
        CreateFailure(
            request,
            startedAt,
            duration,
            outcome,
            phases,
            exception.GetType().Name,
            exception.Message,
            Marshal.GetHRForException(exception));

    private static ProbeResponse CreateFailure(
        ProbeRequest request,
        DateTimeOffset startedAt,
        TimeSpan duration,
        ProbeOutcome outcome,
        IReadOnlyList<ProbePhaseTiming> phases,
        string errorType,
        string errorMessage,
        int? hResult) =>
        new(
            ProbeProtocol.CurrentVersion,
            request.RequestId,
            request.Nonce,
            outcome,
            Environment.ProcessId,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            startedAt,
            duration.TotalMilliseconds,
            phases,
            Menu: null,
            new ProbeError(errorType, errorMessage, hResult));

    private sealed class MenuCaptureState
    {
        public List<ProbeMenuItem> Items { get; } = [];

        public bool Truncated { get; set; }

        public string? Limitation { get; private set; }

        public void MarkPartial(string limitation)
        {
            Truncated = true;
            Limitation ??= limitation;
        }
    }

    private readonly record struct TimedHResult(int HResult, double DurationMilliseconds);
}
