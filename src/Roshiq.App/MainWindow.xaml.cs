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
        App.Host.StateChanged += (_, _) => Dispatcher.InvokeAsync(RefreshUpdateButton);
        App.Host.Settings.Changed += (_, _) => Dispatcher.InvokeAsync(() => { ApplyScale(); UpdateChrome(); });
        Loc.Instance.PropertyChanged += (_, _) => UpdateChrome();
        SetupTray();
        SetupPostMatch();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.K && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true;
                SearchWindow.Toggle(this);
            }
        };
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

    // ------------------------------------------------------------------ обновление программы (кнопка в верхней панели)

    private bool _updateBusy;
    private DispatcherTimer? _updateNoteTimer;

    /// <summary>Вид кнопки: есть новая версия — акцентная «Обновить до …», иначе тихая «Обновления».</summary>
    private void RefreshUpdateButton()
    {
        if (_updateBusy || (_updateNoteTimer?.IsEnabled ?? false)) return;
        var L = Loc.Instance;
        if (App.Host.AvailableUpdate is { } upd)
        {
            UpdateButton.Style = (Style)FindResource("PrimaryButton");
            UpdateText.Text = L.Format("top_update", upd.Tag);
            UpdateButton.ToolTip = L.Get("top_update_tip");
        }
        else
        {
            UpdateButton.Style = (Style)FindResource("GhostButton");
            UpdateText.Text = L.Get("top_update_check");
            UpdateButton.ToolTip = L.Format("top_update_check_tip", AppUpdater.CurrentVersion.ToString(3));
        }
        UpdateButton.Margin = new Thickness(0, 0, 10, 0);
        UpdateButton.Padding = new Thickness(10, 7, 10, 7);
    }

    /// <summary>Короткая надпись на кнопке на 3 секунды («Последняя версия», «Не удалось»).</summary>
    private void FlashUpdateNote(string text)
    {
        UpdateText.Text = text;
        _updateNoteTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _updateNoteTimer.Tick -= UpdateNoteTick;
        _updateNoteTimer.Tick += UpdateNoteTick;
        _updateNoteTimer.Stop();
        _updateNoteTimer.Start();
    }

    private void UpdateNoteTick(object? sender, EventArgs e)
    {
        _updateNoteTimer?.Stop();
        RefreshUpdateButton();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        var L = Loc.Instance;
        var d = App.Host.Settings.Current.Data;
        var upd = App.Host.AvailableUpdate;
        if (upd == null)
        {
            // сеть выключена или не указан репозиторий — ведём туда, где это включается
            if (!d.AllowOnlineStats || string.IsNullOrWhiteSpace(d.UpdateRepo)) { Navigate("settings/data"); return; }
            _updateBusy = true;
            UpdateButton.IsEnabled = false;
            UpdateText.Text = L.Get("top_update_checking");
            try
            {
                var info = await AppUpdater.CheckAsync(d.UpdateRepo!, CancellationToken.None);
                App.Host.SetAvailableUpdate(info);
                _updateBusy = false;
                if (info == null) FlashUpdateNote(L.Get("top_update_latest"));
            }
            catch (Exception ex)
            {
                Log.Warn("Проверка обновления: " + ex.Message);
                _updateBusy = false;
                FlashUpdateNote(L.Get("top_update_failed"));
            }
            finally
            {
                _updateBusy = false;
                UpdateButton.IsEnabled = true;
                RefreshUpdateButton();
            }
            return;
        }

        if (upd.InstallerUrl == null) { AppUpdater.OpenPage(upd.PageUrl); return; }
        var sizeMb = upd.InstallerSize > 0 ? (upd.InstallerSize / 1048576.0).ToString("0.0") : "?";
        if (MessageBox.Show(this, L.Format("upd_app_download_confirm", upd.InstallerName ?? "setup.exe", sizeMb, upd.Tag), "ROSHIQ",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var path = await AppUpdater.DownloadAsync(upd, new Progress<double>(p => UpdateText.Text = L.Format("top_update_progress", (int)Math.Round(p * 100))),
                CancellationToken.None);
            if (MessageBox.Show(this, L.Get("upd_app_run_confirm"), "ROSHIQ", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                AppUpdater.RunInstaller(path);
                RequestExit();
                return;
            }
            MessageBox.Show(this, L.Format("upd_app_saved", path), "ROSHIQ", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Warn("Обновление ROSHIQ: " + ex.Message);
            MessageBox.Show(this, L.Format("upd_check_error", ex.Message), "ROSHIQ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
            RefreshUpdateButton();
        }
    }

    // ------------------------------------------------------------------ итог матча

    private void SetupPostMatch()
    {
        App.Host.PostMatchReady += (_, _) => Dispatcher.InvokeAsync(TryShowPostMatch, DispatcherPriority.ApplicationIdle);
        App.Host.StateChanged += (_, _) => { if (App.Host.PendingPostMatch != null) Dispatcher.InvokeAsync(TryShowPostMatch, DispatcherPriority.ApplicationIdle); };
        Activated += (_, _) => TryShowPostMatch();
    }

    /// <summary>Показать итог матча, когда пользователь не в игре (окно игры не на переднем плане).</summary>
    private void TryShowPostMatch()
    {
        if (_exitRequested || Dispatcher.HasShutdownStarted || App.Host.PendingPostMatch == null) return;
        var game = App.Host.Overlay.Game;
        if (game.Found && game.Foreground && !IsActive) return; // ещё в игре — подождём
        if (OwnedWindows.OfType<Window>().Any(w => w is PostMatchWindow)) return;
        var summary = App.Host.TakePostMatch();
        if (summary == null) return;
        var w = new PostMatchWindow(summary);
        if (IsVisible && WindowState != WindowState.Minimized) w.Owner = this;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
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
            _tray.Menu = BuildTrayMenu;
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

    // Id пунктов меню трея: 1–9 действия, 10–12 режим подсказок, 20–24 роль, 30+ профили оверлея
    private const int TrayPresetBase = 10, TrayRoleBase = 20, TrayProfileBase = 30;

    /// <summary>Меню трея (собирается при каждом открытии — текст и галочки всегда актуальны).</summary>
    private IReadOnlyList<TrayIcon.MenuItem> BuildTrayMenu()
    {
        var L = Loc.Instance;
        var s = App.Host.Settings.Current;
        var presets = new[] { HintPreset.Minimal, HintPreset.Normal, HintPreset.Full }
            .Select((p, i) => new TrayIcon.MenuItem(TrayPresetBase + i, L.Get("preset_" + p.ToString().ToLowerInvariant()), Checked: s.Preset == p)).ToList();
        var roles = Enum.GetValues<Position>()
            .Select((p, i) => new TrayIcon.MenuItem(TrayRoleBase + i, L.PositionName(p), Checked: s.Position == p)).ToList();
        var profiles = s.OverlayProfiles.Select((p, i) => new TrayIcon.MenuItem(TrayProfileBase + i, p.Name, Checked: p.Id == s.ActiveOverlayProfile)).ToList();
        bool overlayShown = !App.Host.SafeMode && App.Host.Overlay.IsVisible;

        var items = new List<TrayIcon.MenuItem>
        {
            new(1, L.Get("tray_open")),
            new(0, "", Separator: true),
            new(2, L.Get(overlayShown ? "tray_overlay_hide" : "tray_overlay_show") + "\t" + s.Overlay.ToggleHotkey, Enabled: !App.Host.SafeMode),
            new(3, L.Get("tray_roshan") + "\t" + s.Overlay.RoshanHotkey),
            new(6, L.Get(App.Host.Overlay.EditMode ? "tray_arrange_done" : "tray_arrange") + "\t" + s.Overlay.EditHotkey, Enabled: !App.Host.SafeMode),
            new(0, L.Get("preset_title"), Children: presets),
            new(0, L.Get("tray_role"), Children: roles),
        };
        if (profiles.Count > 0) items.Add(new(0, L.Get("tray_profile"), Children: profiles));
        items.Add(new(0, "", Separator: true));
        items.Add(new(4, L.Get("tray_diagnostics")));
        items.Add(new(5, L.Get("tray_settings")));
        items.Add(new(0, "", Separator: true));
        items.Add(new(9, L.Get("tray_exit")));
        return items;
    }

    private void OnTrayCommand(int id)
    {
        var s = App.Host.Settings;
        switch (id)
        {
            case 1: RestoreFromTray(); break;
            case 2: App.Host.Overlay.ToggleHidden(); break;
            case 3: App.Host.ToggleRoshanMark(); break;
            case 4: Roshiq.App.Views.DiagnosticsWindow.Open(); break;
            case 5: Navigate("settings"); break;
            case 6: App.Host.Overlay.SetEditMode(!App.Host.Overlay.EditMode); break;
            case 9: RequestExit(); break;
            case >= TrayPresetBase and < TrayPresetBase + 3:
                var preset = (HintPreset)(new[] { (int)HintPreset.Minimal, (int)HintPreset.Normal, (int)HintPreset.Full }[id - TrayPresetBase]);
                s.Update(x => HintPresets.Apply(x, preset));
                App.Host.ShowEventToast("ROSHIQ", Loc.Instance.Format("tray_preset_set", Loc.Instance.Get("preset_" + preset.ToString().ToLowerInvariant())));
                break;
            case >= TrayRoleBase and < TrayProfileBase:
                var positions = Enum.GetValues<Position>();
                int ri = id - TrayRoleBase;
                if (ri < positions.Length) s.Update(x => x.Position = positions[ri]);
                break;
            case >= TrayProfileBase:
                int pi = id - TrayProfileBase;
                if (pi < s.Current.OverlayProfiles.Count) App.Host.ApplyOverlayProfile(s.Current.OverlayProfiles[pi].Id);
                break;
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
        if (Application.Current.MainWindow is not MainWindow w) return;
        // "settings/overlay" — раздел и вкладка внутри него
        var parts = key.Split('/', 2);
        if (parts.Length == 2 && parts[0] == "settings")
        {
            if (Roshiq.App.Views.SettingsHostView.Instance is { } host) host.SelectTab(parts[1]);
            else Roshiq.App.Views.SettingsHostView.PendingTab = parts[1];
        }
        else if (parts.Length == 2 && parts[0] == "assistant")
        {
            if (Roshiq.App.Views.AssistantView.Instance is { } asst) asst.SelectTab(parts[1]);
            else Roshiq.App.Views.AssistantView.PendingTab = parts[1];
        }
        w.Select(parts[0]);
        w.RestoreFromTray();
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
        PageTitle.Text = L.Get("sec_" + _currentKey);
        RefreshUpdateButton();
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

    private void Search_Click(object sender, RoutedEventArgs e) => SearchWindow.Toggle(this);

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
        System.Windows.Input.Keyboard.ClearFocus();
        Host.Content = view;
        Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(Host.Content, view)) ResetPageScroll(view);
        }, DispatcherPriority.Loaded);
        if (view is IRefreshable r) r.Refresh();
        UpdateChrome();
        Motion.Enter(Host, direction * 22, 0);
        Motion.Enter(PageTitle, 0, 9);
        Motion.Enter(PageSubtitle, 0, 6, 50);
    }

    private static void ResetPageScroll(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            // Lists keep their selected item and independent scroll position.
            if (child is System.Windows.Controls.Primitives.Selector) continue;
            if (child is ScrollViewer scroll) { scroll.ScrollToTop(); scroll.ScrollToLeftEnd(); }
            ResetPageScroll(child);
        }
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
