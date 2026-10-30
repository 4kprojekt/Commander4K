using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Commander4K.App.Shell;

/// <summary>
/// The Windows Explorer context menu (Open with, Send to, 7-Zip, Git, Properties…) for files and folders on local
/// disks and UNC shares. Built from the shell's <c>IContextMenu</c>, shown with <c>TrackPopupMenuEx</c>; while it is
/// open the owner window is subclassed so submenus that draw themselves (Send to, Open with) receive their messages
/// through <c>IContextMenu2/3</c>. Must run on the UI (STA) thread.
/// </summary>
internal static partial class ShellContextMenu
{
    private const uint CmfNormal = 0x0, CmfExplore = 0x4, CmfExtendedVerbs = 0x100;
    private const uint TpmReturnCmd = 0x100, TpmRightButton = 0x2;
    private const uint CmicMaskUnicode = 0x4000, CmicMaskPtInvoke = 0x20000000;
    private const int FirstCommand = 1, LastCommand = 0x7FFF;
    private const uint WmInitMenuPopup = 0x117, WmDrawItem = 0x2B, WmMeasureItem = 0x2C, WmMenuChar = 0x120;

    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidContextMenu = new("000214E4-0000-0000-C000-000000000046");

    // The menu being shown, for the subclass procedure (one menu at a time: TrackPopupMenuEx is modal).
    private static IContextMenu2? s_menu2;
    private static IContextMenu3? s_menu3;

    /// <summary>Whether <paramref name="path"/> is something Explorer can show a menu for (drive or UNC path).</summary>
    public static bool Supports(string path) =>
        path.Length >= 3 && ((char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\') || path.StartsWith(@"\\", StringComparison.Ordinal));

    /// <summary>
    /// Shows the menu for <paramref name="paths"/> (all in one folder) at screen point (<paramref name="x"/>,
    /// <paramref name="y"/>) and runs the chosen command. Returns <c>true</c> when a command was run.
    /// </summary>
    public static bool Show(nint owner, IReadOnlyList<string> paths, int x, int y, bool extendedVerbs)
    {
        using var menu = Create(owner, paths, extendedVerbs);
        if (menu is null)
        {
            return false;
        }

        s_menu2 = menu.Menu as IContextMenu2;
        s_menu3 = menu.Menu as IContextMenu3;
        unsafe
        {
            SetWindowSubclass(owner, &SubclassProc, 0x4B43, 0);
        }

        int command;
        try
        {
            command = TrackPopupMenuEx(menu.Handle, TpmReturnCmd | TpmRightButton, x, y, owner, 0);
        }
        finally
        {
            unsafe
            {
                RemoveWindowSubclass(owner, &SubclassProc, 0x4B43);
            }

            s_menu2 = null;
            s_menu3 = null;
        }

        if (command < FirstCommand)
        {
            return false;
        }

        var directory = Marshal.StringToHGlobalUni(Path.GetDirectoryName(paths[0].TrimEnd('\\')) ?? paths[0]);
        try
        {
            var info = new CommandInfo
            {
                Size = (uint)Marshal.SizeOf<CommandInfo>(),
                Mask = CmicMaskUnicode | CmicMaskPtInvoke,
                Window = owner,
                Verb = command - FirstCommand,
                VerbW = command - FirstCommand,
                Directory = 0,
                DirectoryW = directory,
                Show = 1, // SW_SHOWNORMAL
                InvokeX = x,
                InvokeY = y,
            };
            return menu.Menu.InvokeCommand(ref info) >= 0;
        }
        finally
        {
            Marshal.FreeHGlobal(directory);
        }
    }

    /// <summary>The item texts of the menu without showing it (diagnostics and tests).</summary>
    public static IReadOnlyList<string> ListItems(nint owner, IReadOnlyList<string> paths)
    {
        using var menu = Create(owner, paths, extendedVerbs: false);
        if (menu is null)
        {
            return [];
        }

        var items = new List<string>();
        var buffer = new char[260];
        var count = GetMenuItemCount(menu.Handle);
        for (var i = 0; i < count; i++)
        {
            int length;
            unsafe
            {
                fixed (char* text = buffer)
                {
                    length = GetMenuStringW(menu.Handle, (uint)i, text, buffer.Length, 0x400 /* MF_BYPOSITION */);
                }
            }

            if (length > 0)
            {
                items.Add(new string(buffer, 0, length).Replace("&", string.Empty, StringComparison.Ordinal));
            }
        }

        return items;
    }

    private static NativeMenu? Create(nint owner, IReadOnlyList<string> paths, bool extendedVerbs)
    {
        if (paths.Count == 0 || !paths.All(Supports))
        {
            return null;
        }

        var pidls = new List<nint>();
        object? folder = null;
        try
        {
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, 0, out var pidl, 0, out _) < 0 || pidl == 0)
                {
                    return null;
                }

                pidls.Add(pidl);
            }

            // Parent folder of the first item; every item is addressed relative to it (a panel's selection is one folder).
            if (SHBindToParent(pidls[0], IidShellFolder, out var folderPointer, out _) < 0)
            {
                return null;
            }

            folder = Marshal.GetObjectForIUnknown(folderPointer);
            Marshal.Release(folderPointer);
            var children = pidls.Select(ILFindLastID).ToArray();
            if (((IShellFolder)folder).GetUIObjectOf(owner, (uint)children.Length, children, IidContextMenu, 0, out var menuPointer) < 0)
            {
                return null;
            }

            var contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(menuPointer);
            Marshal.Release(menuPointer);
            var handle = CreatePopupMenu();
            contextMenu.QueryContextMenu(handle, 0, FirstCommand, LastCommand, CmfNormal | CmfExplore | (extendedVerbs ? CmfExtendedVerbs : 0));
            return new NativeMenu(handle, contextMenu);
        }
        finally
        {
            foreach (var pidl in pidls)
            {
                Marshal.FreeCoTaskMem(pidl);
            }

            if (folder is not null)
            {
                Marshal.ReleaseComObject(folder);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message is WmInitMenuPopup or WmDrawItem or WmMeasureItem or WmMenuChar)
        {
            try
            {
                if (s_menu3 is { } menu3 && menu3.HandleMenuMsg2(message, wParam, lParam, out var result) >= 0)
                {
                    return result;
                }

                if (s_menu2 is { } menu2 && menu2.HandleMenuMsg(message, wParam, lParam) >= 0)
                {
                    return 0;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // A shell extension misbehaving must not take the window down.
            }
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private sealed class NativeMenu(nint handle, IContextMenu menu) : IDisposable
    {
        public nint Handle { get; } = handle;

        public IContextMenu Menu { get; } = menu;

        public void Dispose()
        {
            DestroyMenu(Handle);
            Marshal.ReleaseComObject(Menu);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandInfo
    {
        public uint Size;
        public uint Mask;
        public nint Window;
        public nint Verb;
        public nint Parameters;
        public nint Directory;
        public int Show;
        public uint HotKey;
        public nint Icon;
        public nint Title;
        public nint VerbW;
        public nint ParametersW;
        public nint DirectoryW;
        public nint TitleW;
        public int InvokeX;
        public int InvokeY;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(nint window, nint bindContext, [MarshalAs(UnmanagedType.LPWStr)] string name, nint eaten, out nint pidl, nint attributes);

        [PreserveSig] int EnumObjects(nint window, int flags, out nint enumerator);

        [PreserveSig] int BindToObject(nint pidl, nint bindContext, in Guid riid, out nint result);

        [PreserveSig] int BindToStorage(nint pidl, nint bindContext, in Guid riid, out nint result);

        [PreserveSig] int CompareIDs(nint parameter, nint pidl1, nint pidl2);

        [PreserveSig] int CreateViewObject(nint owner, in Guid riid, out nint result);

        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] nint[] pidls, ref uint attributes);

        [PreserveSig] int GetUIObjectOf(nint owner, uint count, [MarshalAs(UnmanagedType.LPArray)] nint[] pidls, in Guid riid, nint reserved, out nint result);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint firstCommand, uint lastCommand, uint flags);

        [PreserveSig] int InvokeCommand(ref CommandInfo info);

        [PreserveSig] int GetCommandString(nuint command, uint type, nint reserved, nint name, uint length);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint firstCommand, uint lastCommand, uint flags);

        [PreserveSig] int InvokeCommand(ref CommandInfo info);

        [PreserveSig] int GetCommandString(nuint command, uint type, nint reserved, nint name, uint length);

        [PreserveSig] int HandleMenuMsg(uint message, nint wParam, nint lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint firstCommand, uint lastCommand, uint flags);

        [PreserveSig] int InvokeCommand(ref CommandInfo info);

        [PreserveSig] int GetCommandString(nuint command, uint type, nint reserved, nint name, uint length);

        [PreserveSig] int HandleMenuMsg(uint message, nint wParam, nint lParam);

        [PreserveSig] int HandleMenuMsg2(uint message, nint wParam, nint lParam, out nint result);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributesIn, out uint attributesOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToParent(nint pidl, in Guid riid, out nint result, out nint lastId);

    [LibraryImport("shell32.dll")]
    private static partial nint ILFindLastID(nint pidl);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemCount(nint menu);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetMenuStringW(nint menu, uint item, char* text, int maxCount, uint flags);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetWindowSubclass(nint window, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> procedure, nuint id, nuint data);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool RemoveWindowSubclass(nint window, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> procedure, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out CursorPoint point);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CursorPoint
    {
        public int X;
        public int Y;
    }
}
