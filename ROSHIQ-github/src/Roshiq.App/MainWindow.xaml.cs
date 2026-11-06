using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Roshiq.App.Services;
using Roshiq.App.Views;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Settings;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;

namespace Roshiq.App;

public partial class MainWindow : Window
{
    private static readonly string[] Order = { "dashboard", "draft", "build", "assistant", "history", "settings" };
    private readonly Dictionary<string, Func<UserControl>> _factories;
    private readonly Dictionary<string, UserControl> _cache = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _currentKey = "dashboard";
    private string _lastStatus = "";

    public MainWindow()
    {
        InitializeComponent();
        _factories = new()
        {
            ["dashboard"] = () => new DashboardView(),
            ["draft"] = () => new DraftView(),
            ["build"] = () => new HeroesView(),
            ["assistant"] = () => new AssistantView(),
            ["history"] = () => new HistoryView(),
            ["settings"] = () => new SettingsHostView()
        };
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = v == null ? "v0.1" : $"v{v.Major}.{v.Minor}.{v.Build}";

        ApplyScale();
        Nav.SelectedIndex = 0;
        UpdateChrome();
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        App.Host.StateChanged += (_, _) => UpdateStatus();
        App.Host.Settings.Changed += (_, _) => Dispatcher.InvokeAsync(() => { ApplyScale(); UpdateChrome(); });
        Loc.Instance.PropertyChanged += (_, _) => UpdateChrome();
        SetupTray();
        SetupGlow();
        App.Host.GameLaunched += (_, _) => Dispatcher.InvokeAsync(OnGameLaunched);
        App.Host.GameClosed += (_, _) => Dispatcher.InvokeAsync(OnGameClosed);
    }

    // ------------------------------------------------------------------ запуск и закрытие игры

    private bool _hiddenForGame;

    /// <summary>Дота запустилась: убрать окно с дороги (в трей, а без трея — свернуть). Оверлей работает сам.</summary>
    private void OnGameLaunched()
    {
        var a = App.Host.Settings.Current.Automation;
        if (!a.HideWindowOnGameStart || !IsVisible || WindowState == WindowState.Minimized) return;
        _hiddenForGame = true;
        if (_tray != null) Hide();
        else WindowState = WindowState.Minimized;
    }

    /// <summary>Дота закрылась: вернуть окно — сразу на разбор, если был сыгран матч.</summary>
    private void OnGameClosed()
    {
        var a = App.Host.Settings.Current.Automation;
        bool played = App.Host.MatchSavedSinceGameStart;
        bool bringBack = a.ShowWindowOnGameExit && (_hiddenForGame || played);
        _hiddenForGame = false;
        if (!bringBack) return;
        if (played) Select("history");
        RestoreFromTray();
    }

    // ------------------------------------------------------------------ свечение за курсором

    private const double GlowSize = 220;
    private Point _glowTarget;
    private Point _glowPos;
    private bool _glowFollowing;
    private bool _glowInside;

    private bool GlowEnabled => App.Host.Settings.Current.CursorGlow;

    private void SetupGlow()
    {
        UpdateGlowBrush();
        ThemeManager.Applied += (_, _) => Dispatcher.InvokeAsync(UpdateGlowBrush);
        App.Host.Settings.Changed += (_, _) => Dispatcher.InvokeAsync(() => { if (!GlowEnabled) FadeGlow(0); });
        PreviewMouseMove += (_, e) =>
        {
            if (!GlowEnabled) return;
            _glowTarget = e.GetPosition(GlowLayer);
            if (!_glowInside)
            {
                // первое появление — сразу под курсором, без «прилёта» из угла
                _glowInside = true;
                _glowPos = _glowTarget;
                PlaceGlow();
                FadeGlow(1);
            }
            StartFollowing();
        };
        MouseLeave += (_, _) => { _glowInside = false; FadeGlow(0); };
        Deactivated += (_, _) => { _glowInside = false; FadeGlow(0); };
    }

    /// <summary>Радиальное свечение в акцентном цвете текущей темы.</summary>
    private void UpdateGlowBrush()
    {
        var accent = (Application.Current.Resources["Accent"] as SolidColorBrush)?.Color ?? Color.FromRgb(0xB5, 0xFF, 0x4D);
        Color A(byte alpha) => Color.FromArgb(alpha, accent.R, accent.G, accent.B);
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(A(0x16), 0.0),
                new GradientStop(A(0x0A), 0.35),
                new GradientStop(A(0x03), 0.7),
                new GradientStop(A(0x00), 1.0)
            }
        };
        brush.Freeze();
        CursorGlow.Fill = brush;
    }

    private void FadeGlow(double to)
    {
        var ms = App.Host.Settings.Current.ReduceMotion ? 0 : 220;
        CursorGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));
    }

    private void PlaceGlow()
    {
        Canvas.SetLeft(CursorGlow, _glowPos.X - GlowSize / 2);
        Canvas.SetTop(CursorGlow, _glowPos.Y - GlowSize / 2);
    }

    private void StartFollowing()
    {
        if (_glowFollowing) return;
        _glowFollowing = true;
        CompositionTarget.Rendering += FollowFrame;
    }

    /// <summary>Свечение плавно догоняет курсор (лёгкая инерция), затем отключается до следующего движения.</summary>
    private void FollowFrame(object? sender, EventArgs e)
    {
        double k = App.Host.Settings.Current.ReduceMotion ? 1.0 : 0.22;
        _glowPos = new Point(_glowPos.X + (_glowTarget.X - _glowPos.X) * k, _glowPos.Y + (_glowTarget.Y - _glowPos.Y) * k);
        PlaceGlow();
        if (Math.Abs(_glowTarget.X - _glowPos.X) < 0.5 && Math.Abs(_glowTarget.Y - _glowPos.Y) < 0.5)
        {
            CompositionTarget.Rendering -= FollowFrame;
            _glowFollowing = false;
        }
    }

    // ------------------------------------------------------------------ трей

    private TrayIcon? _tray;
    private bool _exitRequested;

    public bool HasTray => _tray != null;

    /// <summary>Полный выход (не сворачивать в трей).</summary>
    public void RequestExit()
    {
        _exitRequested = true;
        Application.Current.Shutdown();
    }

    private void SetupTray()
    {
        try
        {
            _tray = new TrayIcon();
            _tray.Activated += (_, _) => RestoreFromTray();
            _tray.MenuCommand += (_, id) => OnTrayCommand(id);
            _tray.Menu = () => new List<TrayIcon.MenuItem>
            {
                new(1, Loc.T("tray_open")),
                new(2, Loc.T("tray_overlay") + "\t" + App.Host.Settings.Current.Overlay.ToggleHotkey),
                new(3, Loc.T("tray_roshan") + "\t" + App.Host.Settings.Current.Overlay.RoshanHotkey),
                new(0, "", Separator: true),
                new(9, Loc.T("tray_exit"))
            };
            UpdateTrayIcon();
            ThemeManager.Applied += (_, _) => Dispatcher.InvokeAsync(UpdateTrayIcon);
        }
        catch (Exception ex)
        {
            Log.Error("Значок в трее недоступен", ex);
            _tray?.Dispose();
            _tray = null;
        }
    }

    private void UpdateTrayIcon()
    {
        if (_tray == null) return;
        var brand = ThemeManager.CurrentBrand;
        var path = TrayIcon.ExtractIcon(ThemeManager.IconUri(brand), Path.Combine(AppPaths.Cache, $"tray-{brand}.ico"));
        if (path != null) _tray.Show(path, "ROSHIQ");
    }

    /// <summary>Вернуть окно из трея.</summary>
    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnTrayCommand(int id)
    {
        switch (id)
        {
            case 1: RestoreFromTray(); break;
            case 2: App.Host.Overlay.ToggleHidden(); break;
            case 3: App.Host.ToggleRoshanMark(); break;
            case 9: RequestExit(); break;
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && _tray != null && App.Host.Settings.Current.MinimizeToTray) Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested && _tray != null && App.Host.Settings.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        base.OnClosed(e);
    }

    /// <summary>Перейти к разделу (используется кнопками быстрого доступа).</summary>
    public static void Navigate(string key)
    {
        if (Application.Current.MainWindow is MainWindow w) w.Select(key);
    }

    public void Select(string key)
    {
        int i = Array.IndexOf(Order, key);
        if (i >= 0) Nav.SelectedIndex = i;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
    }

    private void ApplyScale()
    {
        var s = App.Host.Settings.Current.UiScale;
        RootGrid.LayoutTransform = Math.Abs(s - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);
    }

    private void UpdateChrome()
    {
        var L = Loc.Instance;
        int n = Array.IndexOf(Order, _currentKey) + 1;
        PageNumber.Text = $"{n:00} /";
        PageTitle.Text = L.Get("sec_" + _currentKey).ToUpper(L.Language == Roshiq.Core.Models.Language.Russian
            ? new System.Globalization.CultureInfo("ru-RU") : System.Globalization.CultureInfo.InvariantCulture);
        PageSubtitle.Text = L.Get("sec_" + _currentKey + "_sub");

        bool training = App.Host.Settings.Current.UsageContext == UsageContext.Training;
        ContextText.Text = L.Get(training ? "ctx_training_short_chip" : "ctx_reference_short_chip").ToUpperInvariant();
        ContextDot.Fill = (Brush)FindResource(training ? "Warning" : "Accent");
        UpdateStatus();
    }

    /// <summary>Индикатор подключения к Dota 2 и статус матча (по окну игры и данным GSI).</summary>
    private void UpdateStatus()
    {
        var L = Loc.Instance;
        var h = App.Host;
        var game = h.Overlay.Game;
        var gsi = h.LastGsi;
        bool gsiFresh = gsi != null && DateTime.UtcNow - gsi.ReceivedUtc < TimeSpan.FromSeconds(40);

        string key; string brush;
        if (gsiFresh) { key = "top_dota_gsi"; brush = "Accent"; }
        else if (game.Found && game.Minimized) { key = "top_dota_minimized"; brush = "Warning"; }
        else if (game.Found) { key = "top_dota_window"; brush = "Accent"; }
        else { key = "top_dota_off"; brush = "Faint"; }

        StatusText.Text = L.Get(key).ToUpperInvariant();
        StatusDot.Fill = (Brush)FindResource(brush);
        if (key != _lastStatus)
        {
            _lastStatus = key;
            // мягкая анимация смены статуса подключения
            Motion.Enter(StatusDot, 0, 0);
        }

        if (gsiFresh && gsi!.IsHeroSelection) MatchText.Text = L.Get("top_match_draft").ToUpperInvariant();
        else if (gsiFresh && gsi!.IsInProgress && gsi.ClockTime is int c)
        {
            var t = TimeSpan.FromSeconds(Math.Abs(c));
            MatchText.Text = $"{L.Get("top_match_live").ToUpperInvariant()} · {(c < 0 ? "-" : "")}{(int)t.TotalMinutes}:{t.Seconds:00}";
        }
        else if (gsiFresh && gsi!.IsPostGame) MatchText.Text = L.Get("top_match_post").ToUpperInvariant();
        else MatchText.Text = L.Get("top_match_none").ToUpperInvariant();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Select("settings");

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem item || item.Tag is not string key) return;
        int direction = Math.Sign(Array.IndexOf(Order, key) - Array.IndexOf(Order, _currentKey));
        _currentKey = key;
        if (!_cache.TryGetValue(key, out var view))
        {
            view = _factories[key]();
            _cache[key] = view;
        }
        Host.Content = view;
        if (view is IRefreshable r) r.Refresh();
        UpdateChrome();
        Motion.Enter(Host, direction * 22, 0);
        Motion.Enter(PageTitle, 0, 9);
        Motion.Enter(PageSubtitle, 0, 6, 50);
    }


}

/// <summary>Раздел, который умеет обновиться при показе.</summary>
public interface IRefreshable
{
    void Refresh();
}

/// <summary>Тёмный заголовок окна Windows 10/11 (DWM). На старых системах вызов просто игнорируется.</summary>
internal static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            int caption = 0x000A0B09; // COLORREF 0x00BBGGRR = #090B0A — цвет фона
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        }
        catch
        {
            // старая Windows — оставляем системный заголовок
        }
    }
}
