using System.Runtime.InteropServices;

namespace RightMenuCheck.Probe.Worker;

internal static partial class ShellInterop
{
    public const uint ClassContextInProcessServer = 0x1;
    public const uint ClassContextLocalServer = 0x4;
    public const uint CoInitApartmentThreaded = 0x2;
    public const int NoInterface = unchecked((int)0x80004002);
    public const int NotImplemented = unchecked((int)0x80004001);
    public const int FalseResult = 1;
    public const uint MenuItemMaskState = 0x00000001;
    public const uint MenuItemMaskId = 0x00000002;
    public const uint MenuItemMaskSubmenu = 0x00000004;
    public const uint MenuItemMaskString = 0x00000040;
    public const uint MenuItemMaskType = 0x00000100;
    public const uint MenuItemTypeOwnerDrawn = 0x00000100;
    public const uint MenuItemTypeSeparator = 0x00000800;
    public const uint MenuItemStateDisabled = 0x00000003;
    public const uint GetCommandStringVerbUnicode = 0x00000004;
    public const uint GetCommandStringHelpUnicode = 0x00000005;

    public static readonly Guid DataObjectInterfaceId =
        new("0000010E-0000-0000-C000-000000000046");

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(
        ref Guid classId,
        IntPtr outer,
        uint classContext,
        ref Guid interfaceId,
        out IntPtr instance);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHParseDisplayName(
        string name,
        IntPtr bindingContext,
        out IntPtr itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [LibraryImport("shell32.dll")]
    public static partial IntPtr ILClone(IntPtr itemIdList);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ILRemoveLastID(IntPtr itemIdList);

    [LibraryImport("shell32.dll")]
    public static partial IntPtr ILFindLastID(IntPtr itemIdList);

    [LibraryImport("shell32.dll")]
    public static partial int SHCreateDataObject(
        IntPtr folderItemIdList,
        uint childCount,
        IntPtr childItemIdLists,
        IntPtr innerDataObject,
        ref Guid interfaceId,
        out IntPtr result);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHCreateItemFromParsingName(
        string path,
        IntPtr bindingContext,
        ref Guid interfaceId,
        out IntPtr shellItem);

    [LibraryImport("shell32.dll")]
    public static partial int SHCreateShellItemArrayFromShellItem(
        IntPtr shellItem,
        ref Guid interfaceId,
        out IntPtr shellItemArray);

    [LibraryImport("shell32.dll")]
    public static partial int SHCreateDefaultContextMenu(
        ref DefaultContextMenu definition,
        ref Guid interfaceId,
        out IntPtr contextMenu);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyMenu(IntPtr menu);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetMenuItemCount(IntPtr menu);

    [LibraryImport("user32.dll", EntryPoint = "GetMenuItemInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMenuItemInfo(
        IntPtr menu,
        uint item,
        [MarshalAs(UnmanagedType.Bool)] bool byPosition,
        ref MenuItemInfo menuItemInfo);

    [StructLayout(LayoutKind.Sequential)]
    internal struct DefaultContextMenu
    {
        public IntPtr Window;
        public IntPtr Callback;
        public IntPtr FolderItemIdList;
        public IntPtr ShellFolder;
        public uint ChildCount;
        public IntPtr ChildItemIdLists;
        public IntPtr AssociationInfo;
        public uint KeyCount;
        public IntPtr Keys;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuItemInfo
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public IntPtr Submenu;
        public IntPtr CheckedBitmap;
        public IntPtr UncheckedBitmap;
        public UIntPtr ItemData;
        public IntPtr TypeData;
        public uint CharacterCount;
        public IntPtr ItemBitmap;
    }
}

[ComImport]
[Guid("000214E4-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig]
    int QueryContextMenu(
        IntPtr menu,
        uint indexMenu,
        uint firstCommandId,
        uint lastCommandId,
        uint flags);

    [PreserveSig]
    int InvokeCommand(IntPtr commandInfo);

    [PreserveSig]
    int GetCommandString(
        UIntPtr commandId,
        uint type,
        IntPtr reserved,
        IntPtr name,
        uint maximumCharacters);
}

[ComImport]
[Guid("000214E8-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellExtInit
{
    [PreserveSig]
    int Initialize(
        IntPtr folderItemIdList,
        [MarshalAs(UnmanagedType.Interface)] object? dataObject,
        IntPtr programIdKey);
}

[ComImport]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray;

[ComImport]
[Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerCommand
{
    [PreserveSig]
    int GetTitle(IShellItemArray? itemArray, out IntPtr name);

    [PreserveSig]
    int GetIcon(IShellItemArray? itemArray, out IntPtr icon);

    [PreserveSig]
    int GetToolTip(IShellItemArray? itemArray, out IntPtr toolTip);

    [PreserveSig]
    int GetCanonicalName(out Guid commandName);

    [PreserveSig]
    int GetState(
        IShellItemArray? itemArray,
        [MarshalAs(UnmanagedType.Bool)] bool allowSlowOperations,
        out uint state);

    [PreserveSig]
    int Invoke(IShellItemArray? itemArray, IntPtr bindingContext);

    [PreserveSig]
    int GetFlags(out uint flags);

    [PreserveSig]
    int EnumSubCommands(
        [MarshalAs(UnmanagedType.Interface)] out IEnumExplorerCommand? enumerator);
}

[ComImport]
[Guid("A88826F8-186F-4987-AADE-EA0CEF8FBFE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumExplorerCommand
{
    [PreserveSig]
    int Next(
        uint count,
        [MarshalAs(UnmanagedType.Interface)] out IExplorerCommand? command,
        out uint fetched);

    [PreserveSig]
    int Skip(uint count);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone(
        [MarshalAs(UnmanagedType.Interface)] out IEnumExplorerCommand? enumerator);
}
