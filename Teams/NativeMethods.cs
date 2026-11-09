using System.Runtime.InteropServices;
using System.Text;

namespace LoupixDeck.Plugin.MSTeams.Teams;

/// <summary>Win32 calls used to find the Teams windows, wake their accessibility tree and post clicks.</summary>
internal static class NativeMethods
{
    public const uint WM_GETOBJECT = 0x003D;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const nint MK_LBUTTON = 0x0001;
    public const nint OBJID_CLIENT = -4;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    public static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(nint hwnd, ref POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder className, int maxCount);

    public static string GetClassName(nint hwnd)
    {
        StringBuilder sb = new(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>Visible top-level windows owned by any of the given processes.</summary>
    public static List<nint> GetVisibleTopLevelWindows(IReadOnlySet<uint> processIds)
    {
        List<nint> windows = [];
        EnumWindowsProc callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (processIds.Contains(pid) && IsWindowVisible(hwnd))
            {
                windows.Add(hwnd);
            }

            return true;
        };
        EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        return windows;
    }

    /// <summary>All child windows of <paramref name="parent"/>, recursively.</summary>
    public static List<nint> GetChildWindows(nint parent)
    {
        List<nint> children = [];
        EnumWindowsProc callback = (hwnd, _) =>
        {
            children.Add(hwnd);
            return true;
        };
        EnumChildWindows(parent, callback, 0);
        GC.KeepAlive(callback);
        return children;
    }

    /// <summary>
    /// Asks a window for its accessibility object. Chromium only builds (and keeps) its
    /// accessibility tree while a client asks for it, so this has to be repeated.
    /// </summary>
    public static void RequestAccessibility(nint hwnd)
        => SendMessageTimeoutW(hwnd, WM_GETOBJECT, 0, OBJID_CLIENT, SMTO_ABORTIFHUNG, 250, out _);

    public static nint MakeLParam(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));
}
