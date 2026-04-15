using System.Runtime.InteropServices;
using System.Windows;

namespace SpotCont.Infrastructure;

public static class WindowPlacementHelper
{
    private const uint MonitorDefaultToNearest = 2;

    public static Rect GetActiveMonitorWorkArea()
    {
        var foregroundWindow = GetForegroundWindow();
        var monitor = foregroundWindow != IntPtr.Zero
            ? MonitorFromWindow(foregroundWindow, MonitorDefaultToNearest)
            : IntPtr.Zero;

        if (monitor == IntPtr.Zero)
        {
            GetCursorPos(out var point);
            monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        }

        if (monitor == IntPtr.Zero)
        {
            return new Rect(
                SystemParameters.WorkArea.Left,
                SystemParameters.WorkArea.Top,
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height);
        }

        var monitorInfo = new MonitorInfo();
        monitorInfo.cbSize = Marshal.SizeOf<MonitorInfo>();

        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return new Rect(
                SystemParameters.WorkArea.Left,
                SystemParameters.WorkArea.Top,
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height);
        }

        return new Rect(
            monitorInfo.rcWork.Left,
            monitorInfo.rcWork.Top,
            monitorInfo.rcWork.Right - monitorInfo.rcWork.Left,
            monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Win32Point pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Win32Rect rcMonitor;
        public Win32Rect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
