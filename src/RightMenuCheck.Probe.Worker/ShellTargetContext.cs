using System.Runtime.InteropServices;
using RightMenuCheck.Probe.Protocol;

namespace RightMenuCheck.Probe.Worker;

internal sealed class ShellTargetContext : IDisposable
{
    private ShellTargetContext(
        IntPtr folderItemIdList,
        object? dataObject,
        IShellItemArray? itemArray)
    {
        FolderItemIdList = folderItemIdList;
        DataObject = dataObject;
        ItemArray = itemArray;
    }

    public IntPtr FolderItemIdList { get; }

    public object? DataObject { get; }

    public IShellItemArray? ItemArray { get; }

    public static ShellTargetContext CreateClassic(ProbeRequest request)
    {
        ValidateTarget(request);
        var targetItemIdList = ParseDisplayName(request.TargetPath);

        if (request.TargetKind is ProbeTargetKind.FolderBackground or
            ProbeTargetKind.DesktopBackground)
        {
            return new ShellTargetContext(targetItemIdList, dataObject: null, itemArray: null);
        }

        var parentItemIdList = ShellInterop.ILClone(targetItemIdList);
        if (parentItemIdList == IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(targetItemIdList);
            throw new InvalidOperationException("ILClone returned a null PIDL.");
        }

        object? dataObject = null;
        var childArray = IntPtr.Zero;
        try
        {
            if (!ShellInterop.ILRemoveLastID(parentItemIdList))
            {
                throw new InvalidOperationException("The target PIDL has no removable child item.");
            }

            var childItemIdList = ShellInterop.ILFindLastID(targetItemIdList);
            childArray = Marshal.AllocCoTaskMem(IntPtr.Size);
            Marshal.WriteIntPtr(childArray, childItemIdList);
            var interfaceId = ShellInterop.DataObjectInterfaceId;
            var result = ShellInterop.SHCreateDataObject(
                parentItemIdList,
                childCount: 1,
                childArray,
                innerDataObject: IntPtr.Zero,
                ref interfaceId,
                out var dataObjectPointer);
            Marshal.ThrowExceptionForHR(result);
            try
            {
                dataObject = Marshal.GetObjectForIUnknown(dataObjectPointer);
            }
            finally
            {
                _ = Marshal.Release(dataObjectPointer);
            }

            return new ShellTargetContext(parentItemIdList, dataObject, itemArray: null);
        }
        catch
        {
            ReleaseComObject(dataObject);
            Marshal.FreeCoTaskMem(parentItemIdList);
            throw;
        }
        finally
        {
            if (childArray != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(childArray);
            }

            Marshal.FreeCoTaskMem(targetItemIdList);
        }
    }

    public static ShellTargetContext CreateExplorerCommand(ProbeRequest request)
    {
        ValidateTarget(request);
        var shellItemInterfaceId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        var result = ShellInterop.SHCreateItemFromParsingName(
            request.TargetPath,
            bindingContext: IntPtr.Zero,
            ref shellItemInterfaceId,
            out var shellItemPointer);
        Marshal.ThrowExceptionForHR(result);

        try
        {
            var shellItemArrayInterfaceId = typeof(IShellItemArray).GUID;
            result = ShellInterop.SHCreateShellItemArrayFromShellItem(
                shellItemPointer,
                ref shellItemArrayInterfaceId,
                out var shellItemArrayPointer);
            Marshal.ThrowExceptionForHR(result);
            try
            {
                var itemArray = (IShellItemArray)Marshal.GetObjectForIUnknown(
                    shellItemArrayPointer);
                return new ShellTargetContext(
                    folderItemIdList: IntPtr.Zero,
                    dataObject: null,
                    itemArray);
            }
            finally
            {
                _ = Marshal.Release(shellItemArrayPointer);
            }
        }
        finally
        {
            _ = Marshal.Release(shellItemPointer);
        }
    }

    public void Dispose()
    {
        ReleaseComObject(ItemArray);
        ReleaseComObject(DataObject);
        if (FolderItemIdList != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(FolderItemIdList);
        }
    }

    public static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private static IntPtr ParseDisplayName(string path)
    {
        var result = ShellInterop.SHParseDisplayName(
            path,
            bindingContext: IntPtr.Zero,
            out var itemIdList,
            attributesIn: 0,
            out _);
        Marshal.ThrowExceptionForHR(result);
        return itemIdList;
    }

    private static void ValidateTarget(ProbeRequest request)
    {
        var isFileTarget = request.TargetKind == ProbeTargetKind.File;
        if (isFileTarget && !File.Exists(request.TargetPath))
        {
            throw new FileNotFoundException("The file probe target does not exist.", request.TargetPath);
        }

        if (!isFileTarget && !Directory.Exists(request.TargetPath))
        {
            throw new DirectoryNotFoundException("The folder probe target does not exist.");
        }
    }
}
