using System.Runtime.InteropServices;
using RightMenuCheck.Probe.Protocol;

namespace RightMenuCheck.Probe.Worker;

internal sealed class DefaultContextMenuTarget : IDisposable
{
    private readonly IntPtr _targetItemIdList;

    private DefaultContextMenuTarget(
        IntPtr targetItemIdList,
        IntPtr folderItemIdList,
        IntPtr childItemIdLists,
        uint childCount)
    {
        _targetItemIdList = targetItemIdList;
        FolderItemIdList = folderItemIdList;
        ChildItemIdLists = childItemIdLists;
        ChildCount = childCount;
    }

    public IntPtr FolderItemIdList { get; }

    public IntPtr ChildItemIdLists { get; }

    public uint ChildCount { get; }

    public static DefaultContextMenuTarget Create(ProbeRequest request)
    {
        var isFile = request.TargetKind == ProbeTargetKind.File;
        if (isFile && !File.Exists(request.TargetPath))
        {
            throw new FileNotFoundException("The aggregate file target does not exist.", request.TargetPath);
        }

        if (!isFile && !Directory.Exists(request.TargetPath))
        {
            throw new DirectoryNotFoundException("The aggregate folder target does not exist.");
        }

        var result = ShellInterop.SHParseDisplayName(
            request.TargetPath,
            bindingContext: IntPtr.Zero,
            out var targetItemIdList,
            attributesIn: 0,
            out _);
        Marshal.ThrowExceptionForHR(result);

        if (request.TargetKind is ProbeTargetKind.FolderBackground or
            ProbeTargetKind.DesktopBackground)
        {
            return new DefaultContextMenuTarget(
                targetItemIdList: IntPtr.Zero,
                targetItemIdList,
                childItemIdLists: IntPtr.Zero,
                childCount: 0);
        }

        var folderItemIdList = ShellInterop.ILClone(targetItemIdList);
        if (folderItemIdList == IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(targetItemIdList);
            throw new InvalidOperationException("ILClone returned a null aggregate parent PIDL.");
        }

        var childItemIdLists = IntPtr.Zero;
        try
        {
            if (!ShellInterop.ILRemoveLastID(folderItemIdList))
            {
                throw new InvalidOperationException("The aggregate target PIDL has no child item.");
            }

            childItemIdLists = Marshal.AllocCoTaskMem(IntPtr.Size);
            Marshal.WriteIntPtr(
                childItemIdLists,
                ShellInterop.ILFindLastID(targetItemIdList));
            return new DefaultContextMenuTarget(
                targetItemIdList,
                folderItemIdList,
                childItemIdLists,
                childCount: 1);
        }
        catch
        {
            if (childItemIdLists != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(childItemIdLists);
            }

            Marshal.FreeCoTaskMem(folderItemIdList);
            Marshal.FreeCoTaskMem(targetItemIdList);
            throw;
        }
    }

    public void Dispose()
    {
        if (ChildItemIdLists != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(ChildItemIdLists);
        }

        if (FolderItemIdList != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(FolderItemIdList);
        }

        if (_targetItemIdList != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_targetItemIdList);
        }
    }
}
