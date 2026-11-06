using System.Diagnostics;
using Roshiq.Core.Layout;

namespace Roshiq.Overlay;

/// <summary>
/// Находит окно Dota 2 по имени процесса и сообщает его клиентскую область, свёрнутость и фокус.
/// Использует только публичные оконные API (без доступа к памяти процесса).
/// </summary>
public sealed class GameWindowTracker
{
    private IntPtr _hwnd;
    private int _pid;
    private DateTime _lastProcessScan = DateTime.MinValue;

    public string ProcessName { get; set; } = "dota2";
    public IntPtr WindowHandle => _hwnd;

    public GameWindowState Poll()
    {
        try
        {
            if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd))
            {
                _hwnd = IntPtr.Zero;
                // поиск процесса не чаще раза в 2 секунды — дешёво для CPU
                if (DateTime.UtcNow - _lastProcessScan < TimeSpan.FromSeconds(2)) return GameWindowState.NotFound;
                _lastProcessScan = DateTime.UtcNow;
                var proc = Process.GetProcessesByName(ProcessName).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                if (proc == null) return GameWindowState.NotFound;
                _hwnd = proc.MainWindowHandle;
                _pid = proc.Id;
            }

            bool minimized = Native.IsIconic(_hwnd) || !Native.IsWindowVisible(_hwnd);
            var fg = Native.GetForegroundWindow();
            bool foreground = fg == _hwnd;
            if (!foreground && fg != IntPtr.Zero)
            {
                Native.GetWindowThreadProcessId(fg, out var fgPid);
                foreground = fgPid == (uint)_pid;
            }

            if (!Native.GetClientRect(_hwnd, out var rc)) return GameWindowState.NotFound;
            var pt = new Native.POINT { X = 0, Y = 0 };
            Native.ClientToScreen(_hwnd, ref pt);
            var client = new PixelRect(pt.X, pt.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
            return new GameWindowState(true, minimized, foreground, client);
        }
        catch
        {
            _hwnd = IntPtr.Zero;
            return GameWindowState.NotFound;
        }
    }

    public static PixelRect PrimaryScreen() =>
        new(0, 0, Native.GetSystemMetrics(0 /*SM_CXSCREEN*/), Native.GetSystemMetrics(1 /*SM_CYSCREEN*/));
}
