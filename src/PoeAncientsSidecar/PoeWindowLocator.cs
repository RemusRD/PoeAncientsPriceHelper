using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PoeAncientsPriceHelper;

internal static class PoeWindowLocator
{
    private const string Poe2Title = "Path of Exile 2";

    public static bool TryGetForegroundClientRect(out Rectangle clientRect)
        => TryGetForegroundClientRect(out clientRect, out _);

    public static bool TryGetForegroundClientRect(out Rectangle clientRect, out string diagnostic)
    {
        clientRect = Rectangle.Empty;
        diagnostic = "";
        if (!OperatingSystem.IsWindows())
        {
            diagnostic = "not Windows";
            return false;
        }

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            diagnostic = "no foreground window";
            return false;
        }

        var foregroundInfo = WindowInfo.FromHandle(foreground);
        var candidates = FindPoeWindows();
        var handle = FindPoeWindow(foreground, foregroundInfo, candidates, out var matchReason);
        if (handle == IntPtr.Zero)
        {
            diagnostic = $"foreground='{foregroundInfo}' candidates=[{string.Join(" | ", candidates.Select(c => c.ToString()))}]";
            return false;
        }

        if (!TryGetClientRect(handle, out clientRect))
        {
            diagnostic = $"matched {matchReason} but failed client rect for '{WindowInfo.FromHandle(handle)}'";
            return false;
        }

        var screen = Screen.FromRectangle(clientRect);
        diagnostic = $"{matchReason}; foreground='{foregroundInfo}'; matched='{WindowInfo.FromHandle(handle)}'; " +
                     $"client={clientRect}; monitor='{screen.DeviceName}' bounds={screen.Bounds}";
        return true;
    }

    private static IntPtr FindPoeWindow(IntPtr foreground, WindowInfo foregroundInfo, IReadOnlyList<WindowInfo> candidates, out string reason)
    {
        if (IsPoeWindow(foregroundInfo))
        {
            reason = "foreground PoE2 title";
            return foreground;
        }

        uint foregroundProcess = GetWindowProcessId(foreground);
        var sameProcess = candidates.FirstOrDefault(c => c.ProcessId == foregroundProcess);
        if (sameProcess.Handle != IntPtr.Zero)
        {
            reason = "foreground process candidate";
            return sameProcess.Handle;
        }

        reason = "foreground is not PoE2";
        return IntPtr.Zero;
    }

    private static IReadOnlyList<WindowInfo> FindPoeWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (handle == IntPtr.Zero || !IsWindowVisible(handle))
                return true;

            var info = WindowInfo.FromHandle(handle);
            if (IsPoeWindow(info) && TryGetClientRect(handle, out var rect) && rect.Width > 0 && rect.Height > 0)
                result.Add(info);

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsPoeWindow(WindowInfo info)
    {
        if (info.Handle == IntPtr.Zero) return false;
        return LooksLikePoeWindow(info.ProcessName, info.Title);
    }

    internal static bool LooksLikePoeWindow(string process, string title) =>
        IsExactPoe2Title(title);

    private static bool IsExactPoe2Title(string title)
    {
        var trimmed = title.Trim();
        return string.Equals(trimmed, Poe2Title, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        int length = Math.Max(256, GetWindowTextLength(handle) + 1);
        var title = new char[length];
        int len = GetWindowText(handle, title, title.Length);
        return len > 0 ? new string(title, 0, len) : "";
    }

    private static string GetProcessName(uint processId)
    {
        if (processId == 0) return "";
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private readonly record struct WindowInfo(IntPtr Handle, uint ProcessId, string ProcessName, string Title)
    {
        public static WindowInfo FromHandle(IntPtr handle)
        {
            uint processId = GetWindowProcessId(handle);
            return new WindowInfo(handle, processId, GetProcessName(processId), GetWindowTitle(handle));
        }

        public override string ToString()
        {
            var title = string.IsNullOrWhiteSpace(Title) ? "(no title)" : Title;
            var process = string.IsNullOrWhiteSpace(ProcessName) ? "(unknown process)" : ProcessName;
            return $"{process}#{ProcessId} '{title}'";
        }
    }

    private static bool TryGetClientRect(IntPtr handle, out Rectangle rect)
    {
        rect = Rectangle.Empty;
        if (!GetClientRect(handle, out var nativeRect)) return false;

        var topLeft = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(handle, ref topLeft)) return false;

        int width = nativeRect.Right - nativeRect.Left;
        int height = nativeRect.Bottom - nativeRect.Top;
        if (width <= 0 || height <= 0) return false;

        rect = new Rectangle(topLeft.X, topLeft.Y, width, height);
        return true;
    }

    private static uint GetWindowProcessId(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        return processId;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
