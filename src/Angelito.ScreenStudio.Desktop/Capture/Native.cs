using System.Runtime.InteropServices;

namespace Angelito.ScreenStudio.Desktop.Capture;

internal static class Native
{
    public const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}

/// <summary>Global low-level mouse hook. Only position and button-down events; no keyboard, no text, ever.</summary>
internal sealed class MouseHook : IDisposable
{
    private readonly Native.HookProc _proc; // kept alive: the OS holds only a raw pointer
    private IntPtr _hook;
    public event Action<int, int, int>? Event; // x, y (physical desktop px), kind 0..3

    public MouseHook()
    {
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("No se pudo instalar el seguimiento del cursor.");
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var kind = (int)wParam switch
            {
                Native.WM_MOUSEMOVE => 0, Native.WM_LBUTTONDOWN => 1, Native.WM_RBUTTONDOWN => 2, Native.WM_MBUTTONDOWN => 3, _ => -1,
            };
            if (kind >= 0)
            {
                var s = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                Event?.Invoke(s.pt.X, s.pt.Y, kind);
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}

/// <summary>
/// Global low-level keyboard hook that only reports shortcuts the <see cref="KeyFilter"/> allows. Key codes never leave
/// this class unless they form an allowed combination; nothing typed is buffered or stored.
/// </summary>
internal sealed class KeyHook : IDisposable
{
    private readonly Native.HookProc _proc; // kept alive: the OS holds only a raw pointer
    private readonly Angelito.ScreenStudio.Core.KeyFilter _filter;
    private IntPtr _hook;
    private readonly HashSet<uint> _down = [];
    public event Action<string>? Shortcut;
    public bool Suspended => _filter.Suspended;

    public KeyHook(Angelito.ScreenStudio.Core.KeyFilter filter)
    {
        _filter = filter;
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("No se pudo activar el visualizador de atajos.");
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            if (msg is Native.WM_KEYUP or Native.WM_SYSKEYUP) _down.Remove(k.vkCode);
            else if (msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
            {
                var repeat = !_down.Add(k.vkCode); // held keys auto-repeat; show a shortcut once per press
                if (!repeat && !Angelito.ScreenStudio.Core.KeyFilter.IsModifier((int)k.vkCode))
                {
                    var mods = Mods();
                    // Only look up the focused app when the press could be a shortcut (never for plain typing).
                    var maybe = mods != Angelito.ScreenStudio.Core.Mods.None || k.vkCode is >= 0x70 and <= 0x87;
                    var combo = maybe ? _filter.Accept((int)k.vkCode, mods, ForegroundApp()) : null;
                    if (combo != null) Shortcut?.Invoke(combo);
                }
            }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private Angelito.ScreenStudio.Core.Mods Mods()
    {
        var m = Angelito.ScreenStudio.Core.Mods.None;
        // Hardware reports left/right codes; injected input (remote tools, macros) may use the generic 0x10-0x12.
        if (_down.Contains(0xA2) || _down.Contains(0xA3) || _down.Contains(0x11)) m |= Angelito.ScreenStudio.Core.Mods.Ctrl;
        if (_down.Contains(0xA4) || _down.Contains(0x12)) m |= Angelito.ScreenStudio.Core.Mods.Alt;
        if (_down.Contains(0xA5)) m |= Angelito.ScreenStudio.Core.Mods.Alt | Angelito.ScreenStudio.Core.Mods.AltGr; // right Alt = AltGr on many layouts
        if (_down.Contains(0xA0) || _down.Contains(0xA1) || _down.Contains(0x10)) m |= Angelito.ScreenStudio.Core.Mods.Shift;
        if (_down.Contains(0x5B) || _down.Contains(0x5C)) m |= Angelito.ScreenStudio.Core.Mods.Win;
        return m;
    }

    private static string? ForegroundApp()
    {
        try
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return "unknown"; }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}