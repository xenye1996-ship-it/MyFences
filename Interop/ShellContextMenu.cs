using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MyFences.Interop;

internal static class ShellContextMenu
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, nint bind, out nint pidl, uint attributes, out uint output);
    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(nint pidl, ref Guid iid, out IShellFolder parent, out nint child);

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(nint hwnd, nint bind, nint name, nint eaten, nint pidl, nint attributes);
        [PreserveSig] int EnumObjects(nint hwnd, uint flags, out nint result);
        [PreserveSig] int BindToObject(nint pidl, nint bind, ref Guid iid, out nint result);
        [PreserveSig] int BindToStorage(nint pidl, nint bind, ref Guid iid, out nint result);
        [PreserveSig] int CompareIDs(nint parameter, nint first, nint second);
        [PreserveSig] int CreateViewObject(nint hwnd, ref Guid iid, out nint result);
        [PreserveSig] int GetAttributesOf(uint count, nint items, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(nint hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] nint[] items, ref Guid iid, nint reserved, [MarshalAs(UnmanagedType.Interface)] out IContextMenu menu);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo command);
        [PreserveSig] int GetCommandString(nuint id, uint type, nint reserved, nint text, uint size);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo command);
        [PreserveSig] int GetCommandString(nuint id, uint type, nint reserved, nint text, uint size);
        [PreserveSig] int HandleMenuMsg(uint message, nint wp, nint lp);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref CommandInfo command);
        [PreserveSig] int GetCommandString(nuint id, uint type, nint reserved, nint text, uint size);
        [PreserveSig] int HandleMenuMsg(uint message, nint wp, nint lp);
        [PreserveSig] int HandleMenuMsg2(uint message, nint wp, nint lp, out nint result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandInfo
    {
        internal int Size;
        internal uint Mask;
        internal nint Owner, Verb, Parameters, Directory;
        internal int Show;
        internal uint HotKey;
        internal nint Icon, Title, VerbW, ParametersW, DirectoryW, TitleW;
        internal Native.Point InvokePoint;
    }

    internal static void Show(nint owner, string path, int x, int y, bool smokeCheck = false)
    {
        nint pidl = 0, menu = 0;
        IShellFolder? parent = null;
        IContextMenu? context = null;
        HwndSource? source = HwndSource.FromHwnd(owner);
        HwndSourceHook? hook = null;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, 0, out pidl, 0, out _));
            Guid folderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(SHBindToParent(pidl, ref folderId, out parent, out nint child));
            Guid menuId = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(parent.GetUIObjectOf(owner, 1, new[] { child }, ref menuId, 0, out context));
            menu = Native.CreatePopupMenu();
            if (menu == 0) throw new InvalidOperationException("Не удалось создать меню Windows.");
            uint flags = Native.GetKeyState(0x10) < 0 ? 0x100u : 0u;
            Marshal.ThrowExceptionForHR(context.QueryContextMenu(menu, 0, 1, 0x7FFF, flags));
            if (smokeCheck && Native.GetMenuItemCount(menu) < 1) throw new InvalidOperationException("Пустое shell-меню.");
            var menu3 = context as IContextMenu3;
            var menu2 = context as IContextMenu2;
            hook = (nint hwnd, int message, nint wp, nint lp, ref bool handled) =>
            {
                if (message is not (0x117 or 0x2B or 0x2C or 0x120)) return 0;
                if (menu3 != null && menu3.HandleMenuMsg2((uint)message, wp, lp, out nint result) == 0)
                { handled = true; return result; }
                if (menu2 != null && message != 0x120 && menu2.HandleMenuMsg((uint)message, wp, lp) == 0)
                    handled = true;
                return 0;
            };
            source?.AddHook(hook);
            Native.SetForegroundWindow(owner);
            Native.SetFocus(owner);
            uint selected = Native.TrackPopupMenuEx(menu, 0x102, x, y, owner, 0);
            if (selected == 0) return;
            var command = new CommandInfo
            {
                Size = Marshal.SizeOf<CommandInfo>(),
                Mask = 0x4000 | 0x20000000,
                Owner = owner,
                Verb = new nint(selected - 1),
                VerbW = new nint(selected - 1),
                Show = 1,
                InvokePoint = new Native.Point { X = x, Y = y }
            };
            Marshal.ThrowExceptionForHR(context.InvokeCommand(ref command));
        }
        finally
        {
            if (hook != null) source?.RemoveHook(hook);
            if (menu != 0) Native.DestroyMenu(menu);
            if (context != null) Marshal.ReleaseComObject(context);
            if (parent != null) Marshal.ReleaseComObject(parent);
            if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
        }
    }
}
