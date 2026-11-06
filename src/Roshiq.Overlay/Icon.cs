using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Shape = System.Windows.Shapes.Shape;
using WpfPath = System.Windows.Shapes.Path;

namespace Roshiq.Overlay;

/// <summary>
/// Векторная иконка из библиотеки Icons.xaml. Рисуется линией на сетке 24×24 и масштабируется до Size.
/// По умолчанию берёт цвет Foreground ближайшего элемента управления (кнопки, пункта меню, окна),
/// поэтому реагирует на hover/выбор так же, как текст рядом.
/// </summary>
public sealed class Icon : Viewbox
{
    private readonly WpfPath _path;

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(Geometry), typeof(Icon),
        new PropertyMetadata(null, (d, e) => ((Icon)d)._path.Data = e.NewValue as Geometry));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(Icon),
        new PropertyMetadata(null, (d, e) => ((Icon)d).ApplyBrush()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon),
        new PropertyMetadata(18.0, (d, e) => { var i = (Icon)d; i.Width = i.Height = (double)e.NewValue; }));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Icon),
        new PropertyMetadata(1.8, (d, e) => ((Icon)d)._path.StrokeThickness = (double)e.NewValue));

    public Icon()
    {
        _path = new WpfPath
        {
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = false
        };
        Child = _path;
        Width = Height = 18;
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        ApplyBrush();
    }

    public Geometry? Glyph
    {
        get => (Geometry?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Явный цвет. Если не задан — Foreground ближайшего Control.</summary>
    public Brush? Brush
    {
        get => (Brush?)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private void ApplyBrush()
    {
        if (Brush != null)
        {
            BindingOperations.ClearBinding(_path, Shape.StrokeProperty);
            _path.Stroke = Brush;
        }
        else
        {
            _path.SetBinding(Shape.StrokeProperty, new Binding("Foreground")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1)
            });
        }
    }
}
