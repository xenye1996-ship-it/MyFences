using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace MyFences.Interop;

internal sealed class DesktopMouseHook : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Native.MouseProc _callback;
    private nint _hook, _lastWindow;
    private Native.Point _last;
    private uint _lastTime;
    private bool _hasLast;
    private volatile bool _disposed;
    private readonly bool _acceptInjected;
    private Native.Point _selectionStart;
    private nint _selectionWindow;
    private Task<bool>? _selectionCheck;
    private int _selectionButton;
    internal event Action? DoubleClicked;
    internal event Action<System.Drawing.Rectangle>? SelectionFinished;

    internal DesktopMouseHook(Dispatcher dispatcher, bool acceptInjected = false)
    {
        _dispatcher = dispatcher;
        _acceptInjected = acceptInjected;
        _callback = Callback;
        _hook = Native.SetWindowsHookEx(14, _callback, Native.GetModuleHandle(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint Callback(int code, nint wp, nint lp)
    {
        try
        {
            if (code >= 0 && !_disposed && wp.ToInt64() is 0x201 or 0x202 or 0x204 or 0x205)
            {
                var data = Marshal.PtrToStructure<Native.MouseData>(lp);
                if ((data.Flags & 1) == 0 || _acceptInjected)
                {
                    int message = (int)wp;
                    if (message == 0x201) Click(data);
                    if (message is 0x201 or 0x204) BeginSelection(data.Point, message);
                    else if (EndSelection(data.Point, message)) return 1;
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); _hasLast = false; }
        return Native.CallNextHookEx(_hook, code, wp, lp);
    }

    private void BeginSelection(Native.Point point, int button)
    {
        nint hwnd = Native.WindowFromPoint(point);
        _selectionCheck = null;
        _selectionButton = 0;
        if (!DesktopHost.IsSurface(hwnd)) return;
        _selectionStart = point;
        _selectionWindow = hwnd;
        _selectionButton = button;
        _selectionCheck = Task.Run(() =>
        {
            try { return IsEmpty(point, hwnd); }
            catch (Exception ex) { Debug.WriteLine(ex); return false; }
        });
    }

    private bool EndSelection(Native.Point end, int message)
    {
        int expectedDown = message == 0x202 ? 0x201 : 0x204;
        if (_selectionButton != expectedDown || _selectionCheck == null) return false;
        var check = _selectionCheck;
        var start = _selectionStart;
        nint desktop = _selectionWindow;
        _selectionButton = 0;
        _selectionCheck = null;
        if (Math.Abs((long)end.X - start.X) < 60 || Math.Abs((long)end.Y - start.Y) < 60) return false;
        if (!DesktopHost.IsSurface(Native.WindowFromPoint(end))) return false;
        bool rightButton = message == 0x205;
        // A normal right click or a right drag starting on an icon is untouched.
        if (rightButton && (!check.IsCompletedSuccessfully || !check.Result)) return false;
        if (rightButton) Native.PostMessage(desktop, 0x1F, 0, 0);
        _ = CompleteSelectionAsync(check, start, end);
        return rightButton;
    }

    private async Task CompleteSelectionAsync(Task<bool> check, Native.Point start, Native.Point end)
    {
        try
        {
            if (!await check.ConfigureAwait(false)) return;
            await Task.Delay(100).ConfigureAwait(false);
            if (_disposed || _dispatcher.HasShutdownStarted) return;
            var rectangle = System.Drawing.Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
            await _dispatcher.InvokeAsync(() => { if (!_disposed) SelectionFinished?.Invoke(rectangle); });
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void Click(Native.MouseData data)
    {
        nint hwnd = Native.WindowFromPoint(data.Point);
        if (!DesktopHost.IsSurface(hwnd)) { _hasLast = false; return; }
        bool pair = _hasLast && hwnd == _lastWindow &&
            unchecked(data.Time - _lastTime) <= Native.GetDoubleClickTime() &&
            Math.Abs((long)data.Point.X - _last.X) <= Math.Max(1, Native.GetSystemMetrics(36) / 2) &&
            Math.Abs((long)data.Point.Y - _last.Y) <= Math.Max(1, Native.GetSystemMetrics(37) / 2);
        if (pair)
        {
            _hasLast = false;
            _ = ValidateAsync(_last, data.Point, hwnd);
        }
        else { _last = data.Point; _lastTime = data.Time; _lastWindow = hwnd; _hasLast = true; }
    }

    private async Task ValidateAsync(Native.Point first, Native.Point second, nint hwnd)
    {
        try
        {
            bool empty = await Task.Run(() => IsEmpty(first, hwnd) && IsEmpty(second, hwnd)).ConfigureAwait(false);
            if (!empty || _disposed || _dispatcher.HasShutdownStarted) return;
            await _dispatcher.InvokeAsync(() => { if (!_disposed) DoubleClicked?.Invoke(); });
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    internal static bool IsEmpty(Native.Point point, nint hwnd)
    {
        if (Native.WindowFromPoint(point) != hwnd || !DesktopHost.IsSurface(hwnd)) return false;
        AutomationElement? element = AutomationElement.FromPoint(new Point(point.X, point.Y));
        for (int i = 0; i < 32 && element != null; i++)
        {
            var state = element.Current;
            if (state.ControlType == ControlType.ListItem || state.ControlType == ControlType.MenuItem || state.ControlType == ControlType.Button) return false;
            if (state.NativeWindowHandle == hwnd.ToInt64())
                return state.ControlType == ControlType.List || state.ControlType == ControlType.Pane || state.ControlType == ControlType.Window;
            element = TreeWalker.RawViewWalker.GetParent(element);
        }
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_hook != 0 && !Native.UnhookWindowsHookEx(_hook)) Debug.WriteLine(new Win32Exception(Marshal.GetLastWin32Error()));
        _hook = 0;
    }
}
