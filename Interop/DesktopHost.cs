using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MyFences.Interop;

internal static class DesktopHost
{
    internal static nint Find()
    {
        nint shell = Native.GetShellWindow();
        if (shell == 0) throw new InvalidOperationException("Рабочий стол Explorer пока недоступен.");
        Native.GetWindowThreadProcessId(shell, out uint shellPid);
        nint host = 0;
        Native.EnumWindows((hwnd, _) =>
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != shellPid || Native.Class(hwnd) is not ("Progman" or "WorkerW")) return true;
            if (Native.FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null) == 0) return true;
            host = hwnd;
            return false;
        }, 0);
        if (host == 0) throw new InvalidOperationException("Не найден SHELLDLL_DefView рабочего стола.");
        return host;
    }

    internal static nint Attach(nint hwnd)
    {
        nint host = Find();
        long style = Native.GetStyle(hwnd, Native.GwlStyle).ToInt64();
        WriteStyle(hwnd, Native.GwlStyle, (style & ~Native.WsChild) | Native.WsPopup);
        long ex = Native.GetStyle(hwnd, Native.GwlExStyle).ToInt64();
        WriteStyle(hwnd, Native.GwlExStyle, (ex & ~Native.WsExAppWindow) | Native.WsExToolWindow);
        // Keep a genuine top-level WPF rendering surface. Associate it with
        // the shell through ownership, without cross-process reparenting.
        WriteStyle(hwnd, -8, host.ToInt64());
        if (!Native.SetWindowPos(hwnd, new nint(1), 0, 0, 0, 0,
                Native.NoMove | Native.NoSize | Native.NoActivate | Native.FrameChanged))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return host;
    }

    internal static void Place(nint hwnd, nint host, int screenX, int screenY, int width, int height)
    {
        if (!Native.SetWindowPos(hwnd, new nint(1), screenX, screenY, width, height, Native.NoActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static bool IsSurface(nint hwnd)
    {
        nint shell = Native.GetShellWindow();
        if (shell == 0 || hwnd == 0) return false;
        Native.GetWindowThreadProcessId(shell, out uint shellPid);
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != shellPid) return false;
        string name = Native.Class(hwnd);
        if (name == "SysListView32")
        {
            nint view = Native.GetParent(hwnd);
            return Native.Class(view) == "SHELLDLL_DefView" &&
                Native.Class(Native.GetParent(view)) is "Progman" or "WorkerW";
        }
        return name is "Progman" or "WorkerW";
    }

    private static void WriteStyle(nint hwnd, int index, long value)
    {
        Marshal.SetLastPInvokeError(0);
        nint result = Native.SetStyle(hwnd, index, new nint(value));
        int error = Marshal.GetLastPInvokeError();
        if (result == 0 && error != 0) throw new Win32Exception(error);
    }
}
