using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RightMenuCheck.Probe.Protocol;

namespace RightMenuCheck.Probe.Worker;

internal static class ShellProbeExecutor
{
    private const uint ClassContext =
        ShellInterop.ClassContextInProcessServer | ShellInterop.ClassContextLocalServer;
    private const uint MaximumSubCommands = 256;

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
                    firstCommandId: 1,
                    lastCommandId: 0x7FFF,
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
            }
            finally
            {
                _ = ShellInterop.DestroyMenu(menu);
            }

            totalStopwatch.Stop();
            return CreateSuccess(request, startedAt, totalStopwatch.Elapsed, phases);
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

            var titlePointer = IntPtr.Zero;
            var title = Measure(() => command.GetTitle(targetContext.ItemArray, out titlePointer));
            try
            {
                phases.Add(CreatePhase(ProbePhase.GetTitle, title));
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

            var state = Measure(() => command.GetState(
                targetContext.ItemArray,
                allowSlowOperations: false,
                out _));
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

            var enumeration = Measure(() => EnumerateSubCommands(command));
            phases.Add(CreatePhase(
                ProbePhase.EnumerateSubCommands,
                enumeration,
                optionalNotImplemented: true));

            totalStopwatch.Stop();
            return CreateSuccess(request, startedAt, totalStopwatch.Elapsed, phases);
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
                    firstCommandId: 1,
                    lastCommandId: 0x7FFF,
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
            }
            finally
            {
                _ = ShellInterop.DestroyMenu(menu);
            }

            totalStopwatch.Stop();
            return CreateSuccess(request, startedAt, totalStopwatch.Elapsed, phases);
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

    private static int EnumerateSubCommands(IExplorerCommand command)
    {
        var result = command.EnumSubCommands(out var enumerator);
        if (result < 0 || enumerator is null)
        {
            return result;
        }

        try
        {
            for (uint index = 0; index < MaximumSubCommands; index++)
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
                }
                finally
                {
                    ShellTargetContext.ReleaseComObject(childCommand);
                }
            }

            return 0;
        }
        finally
        {
            ShellTargetContext.ReleaseComObject(enumerator);
        }
    }

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
        IReadOnlyList<ProbePhaseTiming> phases) =>
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
            new ProbeError(errorType, errorMessage, hResult));

    private readonly record struct TimedHResult(int HResult, double DurationMilliseconds);
}
