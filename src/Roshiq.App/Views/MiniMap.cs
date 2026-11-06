using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Roshiq.App.Views;

/// <summary>
/// Простая схема карты Dota (без картинок Valve): базы в углах, три линии, река. Точки — в долях карты 0…1,
/// (0,0) — левый нижний угол (база Radiant), (1,1) — правый верхний (база Dire).
/// </summary>
public static class MiniMap
{
    public sealed record Point2(double X, double Y, Brush Fill, string? Tip = null);

    /// <summary>Мировые координаты GSI (примерно −8200…8200) → доли карты.</summary>
    public static (double X, double Y) FromWorld(int x, int y) =>
        (Math.Clamp((x + 8200) / 16400.0, 0, 1), Math.Clamp((y + 8200) / 16400.0, 0, 1));

    public static FrameworkElement Create(IEnumerable<Point2> points, double size = 260)
    {
        var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
        Brush line = Ui.Res("LineStrong");
        Brush faint = Ui.Res("Line");

        // река: диагональ из левого верхнего угла в правый нижний
        canvas.Children.Add(new Line
        {
            X1 = 0, Y1 = 0, X2 = size, Y2 = size, Stroke = new SolidColorBrush(Color.FromArgb(70, 79, 163, 247)),
            StrokeThickness = size * 0.06
        });
        // линии: верх (левый и верхний край), низ (нижний и правый край), центр (диагональ)
        double m = size * 0.1, M = size - m;
        void L(double x1, double y1, double x2, double y2) =>
            canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = line, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        L(m, M, m, m); L(m, m, M, m);       // верхняя
        L(m, M, M, M); L(M, M, M, m);       // нижняя
        L(m + 6, M - 6, M - 6, m + 6);      // центральная
        // базы
        canvas.Children.Add(Base(0, size - size * 0.2, size * 0.2, Color.FromRgb(76, 196, 106)));
        canvas.Children.Add(Base(size - size * 0.2, 0, size * 0.2, Color.FromRgb(236, 91, 86)));

        foreach (var p in points)
        {
            double r = Math.Max(6, size / 36);
            var dot = new Ellipse
            {
                Width = r, Height = r, Fill = p.Fill, Opacity = 0.85,
                Stroke = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), StrokeThickness = 1,
                ToolTip = p.Tip
            };
            Canvas.SetLeft(dot, p.X * size - r / 2);
            Canvas.SetTop(dot, (1 - p.Y) * size - r / 2);
            canvas.Children.Add(dot);
        }
        return new Border
        {
            Width = size + 2, Height = size + 2, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            BorderBrush = faint, Background = Ui.Res("Panel2"), Child = canvas, ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left
        };
    }

    private static Border Base(double left, double top, double size, Color c)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B)), BorderThickness = new Thickness(1)
        };
        Canvas.SetLeft(b, left);
        Canvas.SetTop(b, top);
        return b;
    }
}
