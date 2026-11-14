using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Roshiq.Core.Layout;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Overlay;

namespace Roshiq.App.Views;

/// <summary>
/// 0.24: калибровка экрана — схема экрана игрока в его пропорциях (16:9, 16:10, 21:9…) с зонами интерфейса Dota,
/// виджетом ROSHIQ и карточкой-советом. Виджет ставится щелчком или перетаскиванием, есть готовые места,
/// размер и проверка прямо поверх экрана. Используется в мастере настройки и в настройках оверлея.
/// </summary>
public sealed class ScreenCalibrator : UserControl
{
    public const double PreviewWidth = 520;

    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Border _frame;
    private readonly TextBlock _screenText = new();
    private readonly WrapPanel _places = new();
    private readonly WrapPanel _sizes = new();
    private readonly CheckBox _cardBox = new();
    private readonly Button _testButton = new();
    private readonly DispatcherTimer _testTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private PixelRect _screen;
    private bool _dragging;
    private bool _ready;

    /// <summary>
    /// Готовые места виджета: ключ строки и функция «ширина и полная высота панели (с подробностями) в долях экрана → положение».
    /// Полная высота — чтобы и раскрытый виджет (Ctrl+F8) не залезал на миникарту и панели игры.
    /// </summary>
    public static readonly (string Key, Func<double, double, (double X, double Y)> At)[] Places =
    {
        ("cal_place_right_low", (w, h) => (0.995 - w, 0.86 - h)),
        ("cal_place_right_mid", (w, h) => (0.995 - w, 0.30)),
        ("cal_place_left_map", (w, h) => (0.005, SafeZone.Minimap.Y - 0.01 - h)),
        ("cal_place_right_top", (w, h) => (0.995 - w, SafeZone.TopBar.Bottom + 0.015)),
    };

    public static readonly (string Key, double Scale)[] Sizes = { ("cal_size_s", 0.9), ("cal_size_m", 1.0), ("cal_size_l", 1.2) };

    public ScreenCalibrator()
    {
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = Loc.T("cal_hint"), TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Value"), Margin = new Thickness(0, 0, 0, 10) });
        _screenText.Style = (Style)FindResource("MutedText");
        _screenText.Margin = new Thickness(0, 0, 0, 6);
        root.Children.Add(_screenText);
        _frame = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = Ui.Res("LineStrong"), Background = Ui.Res("Bg"),
            HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true, Child = _canvas, Cursor = Cursors.Hand
        };
        _frame.MouseLeftButtonDown += (_, e) => { _dragging = true; _frame.CaptureMouse(); MoveTo(e.GetPosition(_canvas)); };
        _frame.MouseMove += (_, e) => { if (_dragging) MoveTo(e.GetPosition(_canvas)); };
        _frame.MouseLeftButtonUp += (_, _) => { _dragging = false; _frame.ReleaseMouseCapture(); };
        root.Children.Add(_frame);

        root.Children.Add(new TextBlock { Text = Loc.T("cal_places").ToUpperInvariant(), Style = (Style)FindResource("Eyebrow"), Margin = new Thickness(0, 14, 0, 6) });
        root.Children.Add(_places);
        root.Children.Add(new TextBlock { Text = Loc.T("cal_size").ToUpperInvariant(), Style = (Style)FindResource("Eyebrow"), Margin = new Thickness(0, 8, 0, 6) });
        root.Children.Add(_sizes);
        _cardBox.Content = Loc.T("cal_card_near");
        _cardBox.Margin = new Thickness(0, 8, 0, 0);
        _cardBox.Checked += (_, _) => SetCard(true);
        _cardBox.Unchecked += (_, _) => SetCard(false);
        root.Children.Add(_cardBox);
        root.Children.Add(new TextBlock { Text = Loc.T("cal_card_hint"), Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 2, 0, 10) });
        _testButton.Style = (Style)FindResource("GhostButton");
        _testButton.HorizontalAlignment = HorizontalAlignment.Left;
        _testButton.Click += (_, _) => Test();
        root.Children.Add(_testButton);
        Content = root;

        foreach (var (key, at) in Places)
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Content = Loc.T(key), Margin = new Thickness(0, 0, 8, 8), Tag = key };
            b.Click += (_, _) =>
            {
                var p = SafeZone.Clamp(App.Host.Settings.Current.Overlay.Panel);
                var (x, y) = at(p.W, p.H);
                SetPanel(x, y);
            };
            _places.Children.Add(b);
        }
        foreach (var (key, scale) in Sizes)
        {
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "CalSize" + GetHashCode(), Content = Loc.T(key), Margin = new Thickness(0, 0, 8, 8), Tag = scale };
            rb.Checked += (_, _) => { if (_ready) Update(o => o.Scale = scale); };
            _sizes.Children.Add(rb);
        }
        _testTimer.Tick += (_, _) => StopTest();
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => StopTest();
    }

    /// <summary>Экран, под который калибруем: окно игры, если она запущена, иначе основной монитор.</summary>
    private static PixelRect DetectScreen()
    {
        var g = App.Host.Overlay.Game;
        if (g.Found && !g.ClientArea.IsEmpty) return g.ClientArea;
        var p = GameWindowTracker.PrimaryScreen();
        return p.IsEmpty ? new PixelRect(0, 0, 1920, 1080) : p;
    }

    public void Refresh()
    {
        _ready = false;
        _screen = DetectScreen();
        var o = App.Host.Settings.Current.Overlay;
        _screenText.Text = Loc.Instance.Format(App.Host.Overlay.Game.Found ? "cal_screen_game" : "cal_screen_monitor", _screen.Width, _screen.Height);
        foreach (var rb in _sizes.Children.OfType<RadioButton>())
            rb.IsChecked = Math.Abs((double)rb.Tag - o.Scale) < 0.05;
        _cardBox.IsChecked = o.CardNearWidget;
        _testButton.Content = Loc.T("cal_test");
        _ready = true;
        Draw();
    }

    /// <summary>Видимый размер виджета в долях экрана: ширина из настроек, высота — как у компактного виджета, с учётом масштаба.</summary>
    private (double W, double H) WidgetSize()
    {
        var o = App.Host.Settings.Current.Overlay;
        // компактный виджет ≈ 210 точек по высоте на 1080p; автомасштаб держит ту же долю экрана на больших мониторах
        double height = Math.Max(1, _screen.Height);
        double auto = o.AutoScale ? Math.Max(1.0, height / 1080.0) : 1.0;
        double w = Math.Min(0.45, SafeZone.Clamp(o.Panel).W * o.Scale);
        double h = Math.Min(0.5, 210.0 * auto * o.Scale / height);
        return (w, h);
    }

    private void Draw()
    {
        double cw = PreviewWidth, ch = Math.Round(PreviewWidth * _screen.Height / Math.Max(1, _screen.Width));
        _canvas.Width = cw; _canvas.Height = ch;
        _frame.Width = cw + 2; _frame.Height = ch + 2;
        _canvas.Children.Clear();
        var host = new PixelRect(0, 0, (int)cw, (int)ch);

        void Box(RelRect r, Brush fill, Brush? stroke, string? label, bool dashed = false, Brush? labelBrush = null)
        {
            var px = r.ToPixels(host);
            var rect = new Rectangle
            {
                Width = Math.Max(1, px.Width), Height = Math.Max(1, px.Height), Fill = fill, Stroke = stroke, StrokeThickness = stroke == null ? 0 : 1.5,
                RadiusX = 4, RadiusY = 4, StrokeDashArray = dashed ? new DoubleCollection { 4, 3 } : null
            };
            Canvas.SetLeft(rect, px.X); Canvas.SetTop(rect, px.Y);
            _canvas.Children.Add(rect);
            if (label == null) return;
            var tb = new TextBlock { Text = label, FontSize = 10.5, Foreground = labelBrush ?? Ui.Res("Faint"), FontFamily = (FontFamily)FindResource("DisplayFont"), MaxWidth = Math.Max(20, px.Width - 8), TextTrimming = TextTrimming.CharacterEllipsis };
            Canvas.SetLeft(tb, px.X + 6); Canvas.SetTop(tb, px.Y + 4);
            _canvas.Children.Add(tb);
        }

        Box(SafeZone.Minimap, Ui.Res("Panel3"), null, Loc.T("cal_zone_map"));
        Box(SafeZone.TopBar, Ui.Res("Panel3"), null, Loc.T("cal_zone_top"));
        Box(SafeZone.BottomHud, Ui.Res("Panel3"), null, Loc.T("cal_zone_hud"));

        var o = App.Host.Settings.Current.Overlay;
        var (w, h) = WidgetSize();
        var panel = SafeZone.Clamp(o.Panel);
        var widget = new RelRect(panel.X, panel.Y, Math.Min(w, 1 - panel.X), Math.Min(h, 1 - panel.Y));
        if (o.CardNearWidget)
        {
            var wpx = widget.ToPixels(host);
            var card = CardPlacement.Place(wpx, host, (int)Math.Round(ch * 0.10 * o.Scale), 4);
            Box(RelRect.FromPixels(card, host), Brushes.Transparent, Ui.Res("Accent"), Loc.T("cal_card"), dashed: true, labelBrush: Ui.Res("Accent"));
        }
        else Box(SafeZone.ToastArea, Brushes.Transparent, Ui.Res("LineStrong"), Loc.T("cal_card"), dashed: true);
        Box(widget, Ui.Res("AccentSoft"), Ui.Res("Accent"), "ROSHIQ", labelBrush: Ui.Res("Accent"));
    }

    private void MoveTo(Point p)
    {
        var (w, h) = WidgetSize();
        SetPanel(p.X / Math.Max(1, _canvas.Width) - w / 2, p.Y / Math.Max(1, _canvas.Height) - h / 2);
    }

    private void SetPanel(double x, double y)
    {
        var o = App.Host.Settings.Current.Overlay;
        var rect = SafeZone.Clamp(o.Panel with { X = x, Y = y });
        Update(s => s.Panel = rect);
    }

    private void SetCard(bool on)
    {
        if (_ready) Update(o => o.CardNearWidget = on);
    }

    /// <summary>Сохранить изменение. Калибровка задаёт положение для всех разрешений: старые запомненные положения сбрасываются.</summary>
    private void Update(Action<Roshiq.Core.Settings.OverlaySettings> change)
    {
        App.Host.Settings.Update(s =>
        {
            change(s.Overlay);
            s.Overlay.Layouts.Clear();
            s.Overlay.Calibrated = true;
            if (s.Overlay.Mode == OverlayMode.Hidden) s.Overlay.Mode = OverlayMode.Minimal;
        });
        App.Host.Overlay.Update();
        Draw();
    }

    /// <summary>Показать виджет и пример карточки поверх экрана на 12 секунд.</summary>
    private void Test()
    {
        var ov = App.Host.Overlay;
        ov.TestMode = true;
        Dispatcher.InvokeAsync(() => ov.ShowToast(Loc.T("cal_test_header").ToUpperInvariant(), Loc.T("cal_test_text")), DispatcherPriority.Background);
        _testTimer.Stop();
        _testTimer.Start();
    }

    private void StopTest()
    {
        if (!_testTimer.IsEnabled) return;
        _testTimer.Stop();
        App.Host.Overlay.HideToast();
        App.Host.Overlay.TestMode = false;
    }
}
