using System.Windows.Threading;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Layout;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;

namespace Roshiq.Overlay;

/// <summary>
/// Управляет окнами оверлея: отслеживает окно игры (раз в 500 мс), скрывает оверлей при сворачивании,
/// пересчитывает положение при смене разрешения, обрабатывает F8 и режим настройки.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly SettingsService _settings;
    private readonly GameWindowTracker _tracker = new();
    /// <summary>Окно игры (для запасного захвата картинки драфта).</summary>
    public IntPtr GameWindowHandle => _tracker.WindowHandle;
    private readonly DispatcherTimer _timer;
    private OverlayPanelWindow? _panel;
    private ToastWindow? _toast;
    private HotkeyManager? _hotkeys;
    /// <summary>Ctrl+Shift+R регистрируется только на время драфта, чтобы не перехватывать сочетание в других программах.</summary>
    private HotkeyManager? _draftHotkeys;
    private bool _userHidden;
    private bool _testMode;
    private GameWindowState _lastGame = GameWindowState.NotFound;
    private OverlayVisibility.Decision? _lastDecision;

    public OverlayController(SettingsService settings, OverlayViewModel viewModel)
    {
        _settings = settings;
        ViewModel = viewModel;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Update();
    }

    public OverlayViewModel ViewModel { get; }
    public GameWindowState Game => _lastGame;
    public bool IsVisible => _lastDecision?.Visible == true;
    /// <summary>Автомасштаб под экран, посчитанный при последнем обновлении (1 — игра не найдена или выключен).</summary>
    public double AutoScale { get; private set; } = 1.0;
    /// <summary>Итоговый масштаб содержимого оверлея и уведомлений: автомасштаб × масштаб из настроек.</summary>
    public double EffectiveScale => _settings.Current.Overlay.Scale * (_settings.Current.Overlay.AutoScale ? AutoScale : 1.0);
    public string StatusKey => _lastDecision?.ReasonKey ?? "overlay_game_not_found";
    public bool EditMode => _panel?.EditMode == true;
    public bool TestMode
    {
        get => _testMode;
        set { _testMode = value; Update(); }
    }
    public bool ToggleHotkeyOk { get; private set; }
    public bool RoshanHotkeyOk { get; private set; }
    /// <summary>Сочетания, которые не удалось зарегистрировать (заняты другой программой или не распознаны).</summary>
    public IReadOnlyList<string> FailedHotkeys { get; private set; } = Array.Empty<string>();
    /// <summary>Горячие клавиши уже регистрировались (после Start).</summary>
    public bool HotkeysRegistered => _hotkeys != null;

    /// <summary>Нажата клавиша «Рошан убит» — приложение запускает таймер возрождения.</summary>
    public event EventHandler? RoshanKillMarked;

    public event EventHandler? StateChanged;
    public event EventHandler? DraftRoleRequested;

    public void Start()
    {
        _panel = new OverlayPanelWindow { DataContext = ViewModel };
        _panel.BoundsChangedByUser += OnUserMovedPanel;
        _toast = new ToastWindow();
        RegisterHotkeys();
        _timer.Start();
        Update();
    }

    public void RegisterHotkeys()
    {
        try
        {
            _hotkeys?.Dispose();
            _hotkeys = new HotkeyManager();
            var o = _settings.Current.Overlay;
            var failed = new List<string>();
            ToggleHotkeyOk = _hotkeys.Register(o.ToggleHotkey, ToggleHidden);
            if (!ToggleHotkeyOk) failed.Add(o.ToggleHotkey);
            if (!_hotkeys.Register(o.EditHotkey, () => SetEditMode(!EditMode))) failed.Add(o.EditHotkey);
            if (!_hotkeys.Register(o.DetailsHotkey, ToggleDetails)) failed.Add(o.DetailsHotkey);
            RoshanHotkeyOk = _hotkeys.Register(o.RoshanHotkey, () => RoshanKillMarked?.Invoke(this, EventArgs.Empty));
            if (!RoshanHotkeyOk) failed.Add(o.RoshanHotkey);
            FailedHotkeys = failed;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось зарегистрировать горячие клавиши", ex);
        }
    }

    /// <summary>Панель скрыта игроком клавишей — уведомления поверх игры тоже не показываются.</summary>
    public bool HiddenByUser => _userHidden;

    /// <summary>F8: показать/скрыть панель.</summary>
    public void ToggleHidden()
    {
        var o = _settings.Current.Overlay;
        if (o.Mode == OverlayMode.Hidden)
        {
            o.Mode = OverlayMode.Minimal;
            _userHidden = false;
            _settings.Save();
        }
        else
        {
            _userHidden = !_userHidden;
        }
        if (_userHidden) _toast?.HideToast();
        Update();
    }

    /// <summary>Ctrl+F8: свернуть/развернуть подсказки (Minimal ↔ Expanded) без мыши.</summary>
    public void ToggleDetails()
    {
        var o = _settings.Current.Overlay;
        o.Mode = o.Mode == OverlayMode.Expanded ? OverlayMode.Minimal : OverlayMode.Expanded;
        _userHidden = false;
        _settings.Save();
        Update();
    }

    public void SetEditMode(bool on)
    {
        if (_panel == null) return;
        _panel.EditMode = on;
        if (on) _userHidden = false;
        if (!on) _settings.Save();
        Update();
    }

    public void ShowToast(string text) => ShowToast("ROSHIQ", text);

    public void ShowToast(string header, string text) => ShowToast(header, text, warning: false);

    /// <summary>Показать уведомление поверх игры. false — не показано (оверлей скрыт или игры нет).</summary>
    public bool ShowToast(string header, string text, bool warning)
    {
        if (_toast == null || _userHidden) return false;
        var host = HostRect();
        if (host == null) return false;
        var n = _settings.Current.Notifications;
        _toast.ShowToast(header, text, SafeZone.ToastArea.ToPixels(host.Value), TimeSpan.FromSeconds(n.DisplaySec), _settings.Current.Overlay.Opacity,
            warning, !_settings.Current.ReduceMotion, EffectiveScale);
        return true;
    }

    public void HideToast() => _toast?.HideToast();

    /// <summary>Пересчитать видимость/положение (вызывается таймером и при изменении настроек).</summary>
    public void Update()
    {
        if (_panel == null) return;
        try
        {
            var o = _settings.Current.Overlay;
            _tracker.ProcessName = o.TargetProcess;
            _lastGame = _tracker.Poll();
            if (!EditMode) ApplyLayoutForResolution();
            if (HostRect() is { } screen)
                AutoScale = ScreenScale.Auto(screen.Height, System.Windows.Media.VisualTreeHelper.GetDpi(_panel).DpiScaleY);
            ViewModel.Expanded = o.Mode == OverlayMode.Expanded || EditMode || ViewModel.DraftMode;

            var panel = ViewModel.DraftMode ? o.Panel with { W = Math.Max(o.Panel.W, .24), H = Math.Max(o.Panel.H, Math.Min(.65, 420.0 * (o.AutoScale ? AutoScale : 1.0) / Math.Max(1, _lastGame.ClientArea.Height))) } : o.Panel;
            var mode = ViewModel.DraftMode && o.Mode != OverlayMode.Hidden ? OverlayMode.Expanded : o.Mode;
            var decision = OverlayVisibility.Decide(_lastGame, mode, panel, _userHidden,
                o.HideWhenGameNotFocused || ViewModel.DraftMode, _testMode || EditMode && !_lastGame.Found, GameWindowTracker.PrimaryScreen());

            if (decision.Visible)
            {
                // масштаб: увеличиваем окно от точки привязки и само содержимое, оставаясь в пределах окна игры
                var b = decision.Bounds;
                var host = HostRect() ?? b;
                int w = (int)Math.Round(b.Width * o.Scale), h = (int)Math.Round(b.Height * o.Scale);
                w = Math.Min(w, host.X + host.Width - b.X);
                h = Math.Min(h, host.Y + host.Height - b.Y);
                if (!EditMode) _panel.SetBoundsPixels(b with { Width = Math.Max(1, w), Height = Math.Max(1, h) });
                _panel.SetContentScale(EffectiveScale);
                _panel.SetPanelOpacity(o.Opacity);
                if (!_panel.IsShownOrShowing) _panel.FadeIn();
            }
            else
            {
                if (_panel.IsShownOrShowing) _panel.FadeOut();
                _toast?.HideToast();
            }

            SyncDraftHotkey();
            bool changed = _lastDecision == null || _lastDecision.Visible != decision.Visible || _lastDecision.ReasonKey != decision.ReasonKey;
            _lastDecision = decision;
            if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Оверлей: ошибка обновления", ex);
        }
    }

    private PixelRect? HostRect()
    {
        if (_lastGame.Found && !_lastGame.Minimized && !_lastGame.ClientArea.IsEmpty) return _lastGame.ClientArea;
        if (_testMode) return GameWindowTracker.PrimaryScreen();
        return null;
    }

    private void OnUserMovedPanel(object? sender, PixelRect pixels)
    {
        var host = HostRect();
        if (host == null) return;
        var o = _settings.Current.Overlay;
        var rel = RelRect.FromPixels(pixels, host.Value);
        // в режиме Minimal высоту задаёт сам режим, сохраняем только позицию
        if (o.Mode == OverlayMode.Minimal) rel = rel with { H = o.Panel.H };
        if (o.SnapToEdges) rel = LayoutMemory.Snap(rel);
        o.Panel = SafeZone.Clamp(rel);
        // запомнить для текущего разрешения окна игры
        if (_layoutKey != null) o.Layouts[_layoutKey] = o.Panel;
    }

    private string? _layoutKey;

    /// <summary>Разрешение окна игры, для которого сейчас применено положение ("2560x1440"), или null.</summary>
    public string? CurrentLayoutKey => _layoutKey;

    /// <summary>Сменилось разрешение окна игры — взять сохранённое для него положение (если есть).</summary>
    private void ApplyLayoutForResolution()
    {
        if (!_lastGame.Found) return;
        var key = LayoutMemory.Key(_lastGame.ClientArea.Width, _lastGame.ClientArea.Height);
        if (key == null || key == _layoutKey) return;
        _layoutKey = key;
        var o = _settings.Current.Overlay;
        if (o.Layouts.TryGetValue(key, out var saved) && saved != o.Panel)
        {
            o.Panel = SafeZone.Clamp(saved);
            Log.Info($"Оверлей: положение для {key}");
        }
        else if (!o.Layouts.ContainsKey(key)) o.Layouts[key] = o.Panel;
    }

    /// <summary>Забыть сохранённые положения для всех разрешений (кроме текущего).</summary>
    public void ResetLayouts()
    {
        var o = _settings.Current.Overlay;
        o.Layouts.Clear();
        if (_layoutKey != null) o.Layouts[_layoutKey] = o.Panel;
        _settings.Save();
    }

    private void SyncDraftHotkey()
    {
        if (ViewModel.DraftMode && _draftHotkeys == null)
        {
            try
            {
                _draftHotkeys = new HotkeyManager();
                _draftHotkeys.Register("Ctrl+Shift+R", () => { if (ViewModel.DraftMode) DraftRoleRequested?.Invoke(this, EventArgs.Empty); });
            }
            catch (Exception ex) { Log.Error("Не удалось зарегистрировать Ctrl+Shift+R", ex); }
        }
        else if (!ViewModel.DraftMode && _draftHotkeys != null)
        {
            var old = _draftHotkeys;
            _draftHotkeys = null;
            // освобождаем вне обработчика сообщения, который мог вызвать Update
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(old.Dispose);
        }
    }

    public void Dispose()
    {
        _draftHotkeys?.Dispose();
        _timer.Stop();
        _hotkeys?.Dispose();
        _panel?.Close();
        _toast?.Close();
    }
}
