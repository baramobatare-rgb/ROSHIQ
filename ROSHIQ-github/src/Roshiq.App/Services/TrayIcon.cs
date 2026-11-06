using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Roshiq.Core.Diagnostics;

namespace Roshiq.App.Services;

/// <summary>
/// Значок в области уведомлений через Shell_NotifyIcon (без WinForms).
/// Левый клик — открыть окно, правый — меню. После перезапуска Проводника значок добавляется заново.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public sealed record MenuItem(int Id, string Text, bool Separator = false);

    private const int WM_APP_TRAY = 0x8000 + 0x51;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const int IMAGE_ICON = 1;
    private const int LR_LOADFROMFILE = 0x10;
    private const int SM_CXSMICON = 49, SM_CYSMICON = 50;
    private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, int type, int cx, int cy, int load);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessageW(string name);

    private readonly HwndSource _source;
    private readonly int _taskbarCreated;
    private IntPtr _icon;
    private string _tip = "ROSHIQ";
    private bool _added;

    public TrayIcon()
    {
        var p = new HwndSourceParameters("RoshiqTray") { Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    }

    /// <summary>Открыть окно (левый клик по значку).</summary>
    public event EventHandler? Activated;
    /// <summary>Выбран пункт меню (Id из Menu).</summary>
    public event EventHandler<int>? MenuCommand;

    /// <summary>Пункты меню; запрашиваются при каждом открытии, чтобы текст был актуальным.</summary>
    public Func<IReadOnlyList<MenuItem>>? Menu { get; set; }

    /// <summary>Показать значок из .ico-файла (с диска).</summary>
    public void Show(string iconPath, string tip)
    {
        _tip = tip.Length > 120 ? tip[..120] : tip;
        var newIcon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_LOADFROMFILE);
        if (newIcon == IntPtr.Zero)
        {
            Log.Warn("Трей: не удалось загрузить иконку " + iconPath);
            if (_icon == IntPtr.Zero) return;
        }
        else
        {
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
            _icon = newIcon;
        }
        Update(_added ? NIM_MODIFY : NIM_ADD);
    }

    private void Update(int message)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = _tip,
            szInfo = "",
            szInfoTitle = ""
        };
        bool ok = Shell_NotifyIconW(message, ref data);
        if (message == NIM_ADD) _added = ok;
        if (message == NIM_DELETE) _added = false;
        if (!ok && message != NIM_DELETE) Log.Warn("Трей: Shell_NotifyIcon вернул ошибку (" + message + ")");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Проводник перезапущен — значок нужно добавить заново
            _added = false;
            if (_icon != IntPtr.Zero) Update(NIM_ADD);
            return IntPtr.Zero;
        }
        if (msg != WM_APP_TRAY) return IntPtr.Zero;
        int mouse = (int)(lParam.ToInt64() & 0xFFFF);
        switch (mouse)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                handled = true;
                Activated?.Invoke(this, EventArgs.Empty);
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                handled = true;
                ShowMenu();
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var items = Menu?.Invoke();
        if (items == null || items.Count == 0) return;
        var menu = CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (item.Separator) AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
                else AppendMenuW(menu, MF_STRING, new IntPtr(item.Id), item.Text);
            }
            GetCursorPos(out var pt);
            // без этого меню не закрывается при клике мимо (документированное поведение Windows)
            SetForegroundWindow(_source.Handle);
            int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, 0, _source.Handle, IntPtr.Zero);
            PostMessageW(_source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            if (cmd > 0) MenuCommand?.Invoke(this, cmd);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        try { if (_added) Update(NIM_DELETE); } catch { }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    /// <summary>Сохранить .ico из ресурсов приложения на диск (Shell_NotifyIcon нужен HICON, а LoadImage читает файл).</summary>
    public static string? ExtractIcon(Uri packUri, string targetPath)
    {
        try
        {
            var info = Application.GetResourceStream(packUri);
            if (info == null) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using (var s = info.Stream)
            using (var f = File.Create(targetPath))
                s.CopyTo(f);
            return targetPath;
        }
        catch (Exception ex)
        {
            Log.Warn("Трей: не удалось подготовить иконку: " + ex.Message);
            return File.Exists(targetPath) ? targetPath : null;
        }
    }
}
