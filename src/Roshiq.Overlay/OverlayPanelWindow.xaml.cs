using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Roshiq.Core.Layout;

namespace Roshiq.Overlay;

/// <summary>
/// Прозрачное окно-панель поверх игры. В обычном режиме полностью "прозрачно" для мыши (WS_EX_TRANSPARENT)
/// и никогда не забирает фокус. В режиме настройки его можно перетаскивать и менять размер.
/// Появление/скрытие — плавное (≈200 мс), смена главной рекомендации подсвечивается индикатором.
/// </summary>
public partial class OverlayPanelWindow : Window
{
    private IntPtr _hwnd;
    private bool _editMode;
    private bool _hiding;
    private double _targetOpacity = 0.9;
    private OverlayViewModel? _vm;

    public OverlayPanelWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnMouseDown;
        SizeChanged += (_, _) => { if (_editMode) RaiseBoundsChanged(); };
        LocationChanged += (_, _) => { if (_editMode) RaiseBoundsChanged(); };
        DataContextChanged += (_, e) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as OverlayViewModel;
            if (_vm != null)
            {
                _vm.PropertyChanged += OnVmChanged;
                UpdateGold();
                UpdateUrgent();
            }
        };
    }

    /// <summary>Пользователь переместил/изменил панель (в физических пикселях экрана).</summary>
    public event EventHandler<PixelRect>? BoundsChangedByUser;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetClickThrough(_hwnd, true);
        // окно оверлея не попадает в собственный захват экрана (Windows 10 2004+)
        try { Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE); } catch { }
    }

    public bool EditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            if (_hwnd != IntPtr.Zero) Native.SetClickThrough(_hwnd, !value);
            ResizeMode = value ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
            if (DataContext is OverlayViewModel vm) vm.EditMode = value;
        }
    }

    public void SetPanelOpacity(double opacity)
    {
        _targetOpacity = Math.Clamp(opacity, 0.2, 1.0);
        if (IsVisible && !_hiding && Root.Opacity > 0.01 && Root.HasAnimatedProperties == false) Root.Opacity = _targetOpacity;
    }

    /// <summary>Масштаб содержимого (0.8–1.5).</summary>
    public void SetContentScale(double scale)
    {
        scale = Math.Clamp(scale, 0.8, 1.5);
        ContentScale.ScaleX = scale;
        ContentScale.ScaleY = scale;
    }

    /// <summary>Плавно показать окно.</summary>
    public void FadeIn()
    {
        _hiding = false;
        if (!IsVisible)
        {
            Root.Opacity = 0;
            Show();
        }
        var anim = new DoubleAnimation(_targetOpacity, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        anim.Completed += (_, _) => { if (!_hiding) { Root.BeginAnimation(OpacityProperty, null); Root.Opacity = _targetOpacity; } };
        Root.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>Плавно скрыть окно.</summary>
    public void FadeOut()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        anim.Completed += (_, _) =>
        {
            if (!_hiding) return;
            Hide();
            Root.BeginAnimation(OpacityProperty, null);
            Root.Opacity = 0;
            _hiding = false;
        };
        Root.BeginAnimation(OpacityProperty, anim);
    }

    public bool IsShownOrShowing => IsVisible && !_hiding;

    /// <summary>Разместить окно по прямоугольнику в физических пикселях с учётом DPI.</summary>
    public void SetBoundsPixels(PixelRect r)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double sy = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        bool wasEdit = _editMode;
        _editMode = false; // не считать программное перемещение действием пользователя
        Left = r.X / sx;
        Top = r.Y / sy;
        Width = Math.Max(MinWidth, r.Width / sx);
        Height = Math.Max(MinHeight, r.Height / sy);
        _editMode = wasEdit;
    }

    public PixelRect GetBoundsPixels()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new PixelRect((int)Math.Round(Left * dpi.DpiScaleX), (int)Math.Round(Top * dpi.DpiScaleY),
            (int)Math.Round(ActualWidth * dpi.DpiScaleX), (int)Math.Round(ActualHeight * dpi.DpiScaleY));
    }

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>
    /// Анимации панели: новая рекомендация мягко «въезжает», индикатор дважды мигает; полоска золота плавно
    /// растёт; строка таймеров «дышит», когда до события меньше 10 секунд.
    /// </summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.MainTitle):
                if (!IsVisible) return;
                var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(220)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) };
                PulseDot.BeginAnimation(OpacityProperty, pulse);
                if (_vm.Animate)
                {
                    MainBlock.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ease });
                    MainShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ease });
                }
                break;
            case nameof(OverlayViewModel.GoldProgress):
                UpdateGold();
                break;
            case nameof(OverlayViewModel.TimersUrgent):
            case nameof(OverlayViewModel.Animate):
                UpdateUrgent();
                break;
        }
    }

    private void UpdateGold()
    {
        if (_vm == null) return;
        double p = _vm.GoldProgress;
        if (p < 0)
        {
            GoldBar.Visibility = Visibility.Collapsed;
            return;
        }
        GoldBar.Visibility = Visibility.Visible;
        if (_vm.Animate)
            GoldScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(p, TimeSpan.FromMilliseconds(420)) { EasingFunction = Ease });
        else
        {
            GoldScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            GoldScale.ScaleX = p;
        }
    }

    private void UpdateUrgent()
    {
        if (_vm == null) return;
        if (_vm.TimersUrgent && _vm.Animate)
            TimersText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(450))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        else
            TimersText.BeginAnimation(OpacityProperty, null);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void RaiseBoundsChanged() => BoundsChangedByUser?.Invoke(this, GetBoundsPixels());
}
