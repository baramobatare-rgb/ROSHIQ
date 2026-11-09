using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Roshiq.Core.Layout;

namespace Roshiq.Overlay;

/// <summary>Тихое напоминание: без звука, без фокуса, прозрачно для мыши, плавно появляется и само исчезает.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _hideTimer = new();
    private double _opacity = 0.95;

    public ToastWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => HideToast();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetClickThrough(hwnd, true);
        try { Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE); } catch { }
    }

    private static readonly SolidColorBrush WarningBrush = CreateWarning();

    private static SolidColorBrush CreateWarning()
    {
        var b = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x57));
        b.Freeze();
        return b;
    }

    /// <param name="warning">Предупреждение (нет TP, байбэк, отстаёте от темпа): жёлтая точка и рамка значка.</param>
    /// <param name="animate">Плавно «въехать» справа (выключается настройкой «Уменьшить анимацию»).</param>
    public void ShowToast(string header, string text, PixelRect bounds, TimeSpan duration, double opacity, bool warning = false, bool animate = true, double scale = 1.0)
    {
        HeaderText.Text = header;
        Message.Text = text;
        if (warning)
        {
            Dot.Fill = WarningBrush;
            IconBox.BorderBrush = WarningBrush;
            HeaderText.Foreground = WarningBrush;
        }
        else
        {
            Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
            IconBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x33, 0x2E));
            HeaderText.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x94, 0x8E));
        }
        _opacity = Math.Clamp(opacity, 0.2, 1.0);
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = bounds.X / dpi.DpiScaleX;
        Top = bounds.Y / dpi.DpiScaleY;
        // масштаб под экран: увеличиваем саму карточку, окно — не меньше её размера
        scale = double.IsFinite(scale) ? Math.Clamp(scale, 0.6, 3.0) : 1.0;
        Root.LayoutTransform = Math.Abs(scale - 1.0) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
        Width = Math.Max(240 * scale, bounds.Width / dpi.DpiScaleX);
        Height = Math.Max(68 * scale, bounds.Height / dpi.DpiScaleY);
        if (!IsVisible)
        {
            Root.Opacity = 0;
            Show();
        }
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(_opacity, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (animate)
            RootShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
            RootShift.BeginAnimation(TranslateTransform.XProperty, null);
        _hideTimer.Stop();
        _hideTimer.Interval = duration;
        _hideTimer.Start();
    }

    public void HideToast()
    {
        _hideTimer.Stop();
        if (!IsVisible) return;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        anim.Completed += (_, _) => { if (Root.Opacity < 0.05) Hide(); };
        Root.BeginAnimation(OpacityProperty, anim);
    }
}
