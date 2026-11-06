using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Roshiq.App;

/// <summary>Short, finite render-only animations; no layout animation or idle render loop.</summary>
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation && App.Host is { } host && !host.Settings.Current.ReduceMotion;
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(string), typeof(Motion), new PropertyMetadata(null, OnKindChanged));
    public static void SetKind(DependencyObject target, string value) => target.SetValue(KindProperty, value);
    public static string? GetKind(DependencyObject target) => (string?)target.GetValue(KindProperty);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.Loaded -= Loaded;
        element.Unloaded -= Unloaded;
        element.MouseEnter -= Hover;
        element.MouseLeave -= Hover;
        element.PreviewMouseLeftButtonDown -= Press;
        element.PreviewMouseLeftButtonUp -= Release;
        element.LostMouseCapture -= Release;
        if (e.NewValue is not string) return;
        element.Loaded += Loaded;
        element.Unloaded += Unloaded;
        if ((string)e.NewValue == "Button")
        {
            element.MouseEnter += Hover;
            element.MouseLeave += Hover;
            element.PreviewMouseLeftButtonDown += Press;
            element.PreviewMouseLeftButtonUp += Release;
            element.LostMouseCapture += Release;
        }
    }

    private static void Loaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (GetKind(element) != "Card") return;
        // Position in the containing page gives stable stagger even for cached views.
        int index = 0;
        DependencyObject root = element;
        for (var parent = VisualTreeHelper.GetParent(root); parent != null && parent is not UserControl;
             parent = VisualTreeHelper.GetParent(root)) root = parent;
        foreach (var card in Descendants(root).Where(x => GetKind(x) == "Card"))
        {
            if (ReferenceEquals(card, element)) break;
            index++;
        }
        Enter(element, 0, 18, Math.Min(index * 45, 225));
    }

    private static readonly ConditionalWeakTable<TextBlock, EventHandler> Counters = new();

    /// <summary>
    /// Число «набегает» от 0 до значения за ~0,6 с (ease-out). Повторный вызов отменяет предыдущий счёт.
    /// При выключенных анимациях значение ставится сразу.
    /// </summary>
    public static void CountUp(TextBlock target, double to, string format = "0", string suffix = "", int ms = 650)
    {
        if (Counters.TryGetValue(target, out var running))
        {
            CompositionTarget.Rendering -= running;
            Counters.Remove(target);
        }
        string Text(double v) => v.ToString(format, CultureInfo.InvariantCulture) + suffix;
        if (!Enabled) { target.Text = Text(to); return; }
        var start = DateTime.UtcNow;
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            double t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / ms);
            double eased = 1 - Math.Pow(1 - t, 3);
            target.Text = Text(to * eased);
            if (t >= 1)
            {
                CompositionTarget.Rendering -= tick;
                Counters.Remove(target);
                target.Text = Text(to);
            }
        };
        Counters.AddOrUpdate(target, tick);
        target.Text = Text(0);
        CompositionTarget.Rendering += tick;
    }

    public static void Enter(FrameworkElement element, double x, double y, int delay = 0)
    {
        Reset(element);
        if (!Enabled) return;
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        Animate(element, UIElement.OpacityProperty, 1, 280, 0, delay);
        Animate(shift, TranslateTransform.XProperty, 0, 340, x, delay);
        Animate(shift, TranslateTransform.YProperty, 0, 340, y, delay);
    }

    private static TransformGroup ButtonTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group) return group;
        group = new TransformGroup();
        group.Children.Add(new ScaleTransform(1, 1));
        group.Children.Add(new TranslateTransform());
        element.RenderTransformOrigin = new Point(.5, .5);
        element.RenderTransform = group;
        return group;
    }

    private static void Hover(object sender, MouseEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (!Enabled || !element.IsEnabled) { Reset(element); return; }
        var group = ButtonTransform(element);
        Animate((TranslateTransform)group.Children[1], TranslateTransform.YProperty,
            element.IsMouseOver ? -2 : 0, 160);
        if (!element.IsMouseOver) Scale(element, 1);
    }
    private static void Press(object sender, MouseButtonEventArgs e)
    {
        if (sender is ButtonBase button && button.IsEnabled && Enabled) Scale(button, .965);
    }
    private static void Release(object sender, MouseEventArgs e) => Scale((FrameworkElement)sender, 1);
    private static void Scale(FrameworkElement element, double value)
    {
        if (!Enabled) { Reset(element); return; }
        var scale = (ScaleTransform)ButtonTransform(element).Children[0];
        Animate(scale, ScaleTransform.ScaleXProperty, value, 130);
        Animate(scale, ScaleTransform.ScaleYProperty, value, 130);
    }
    private static void Animate(Animatable target, DependencyProperty property, double to, int ms, double? from = null, int delay = 0)
    {
        var animation = new DoubleAnimation { To = to, From = from ?? (double)target.GetValue(property), Duration = TimeSpan.FromMilliseconds(ms),
            BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop };
        target.SetValue(property, to);
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private static void Animate(UIElement target, DependencyProperty property, double to, int ms, double from, int delay)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > 0)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + ms)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
    private static void Unloaded(object sender, RoutedEventArgs e) => Reset((FrameworkElement)sender);
    private static void Reset(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (element.RenderTransform is TranslateTransform shift)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
        }
        if (element.RenderTransform is TransformGroup group)
            foreach (var transform in group.Children)
            {
                if (transform is ScaleTransform scale)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    scale.ScaleX = scale.ScaleY = 1;
                }
                if (transform is TranslateTransform move) {
                    move.BeginAnimation(TranslateTransform.YProperty, null); move.Y = 0;
                }
            }
    }
    public static void ResetTree(FrameworkElement root)
    {
        Reset(root);
        foreach (var element in Descendants(root)) Reset(element);
    }
    private static IEnumerable<FrameworkElement> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element) yield return element;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
