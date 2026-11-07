using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Roshiq.Core.Localization;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

/// <summary>
/// Схема карты Dota, нарисованная кодом (без картинок Valve): половины сторон, река, лес, три линии с вышками,
/// древние, логова Рошана. Точки — в долях карты 0…1, (0,0) — левый нижний угол (база Radiant), (1,1) — правый верхний (Dire).
/// По желанию — тепловой слой: где точек больше, там пятно ярче.
/// </summary>
public static class MiniMap
{
    public sealed record Point2(double X, double Y, Brush Fill, string? Tip = null);

    /// <summary>Мировые координаты GSI (примерно −8200…8200) → доли карты.</summary>
    public static (double X, double Y) FromWorld(int x, int y) => MapZones.FromWorld(x, y);

    private static readonly Color RadiantC = Color.FromRgb(76, 196, 106);
    private static readonly Color DireC = Color.FromRgb(236, 91, 86);
    private static readonly Color RiverC = Color.FromRgb(79, 163, 247);

    public static FrameworkElement Create(IEnumerable<Point2> points, double size = 260, bool heat = false, bool labels = false)
    {
        var canvas = new Canvas { Width = size, Height = size, ClipToBounds = true };
        double S(double v) => v * size;
        Point P(double x, double y) => new(S(x), S(1 - y));

        // половины сторон: лёгкий оттенок своей команды
        canvas.Children.Add(Poly(new[] { P(0, 0), P(0, 1), P(1, 0) }, Color.FromArgb(18, RadiantC.R, RadiantC.G, RadiantC.B)));
        canvas.Children.Add(Poly(new[] { P(1, 1), P(0, 1), P(1, 0) }, Color.FromArgb(18, DireC.R, DireC.G, DireC.B)));

        // лес: несколько пятен на каждой стороне (схематично, зеркально)
        foreach (var (x, y, w, h) in new[] { (0.22, 0.30, 0.16, 0.12), (0.40, 0.18, 0.18, 0.10), (0.16, 0.52, 0.10, 0.14), (0.62, 0.20, 0.12, 0.10) })
        {
            canvas.Children.Add(Patch(S(x), S(1 - y - h), S(w), S(h), RadiantC));
            canvas.Children.Add(Patch(S(1 - x - w), S(y), S(w), S(h), DireC));
        }

        // река: плавная кривая из левого верхнего угла в правый нижний
        var river = new PathFigure { StartPoint = P(0.02, 0.98) };
        river.Segments.Add(new BezierSegment(P(0.30, 0.78), P(0.45, 0.58), P(0.50, 0.50), true));
        river.Segments.Add(new BezierSegment(P(0.55, 0.42), P(0.70, 0.22), P(0.98, 0.02), true));
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = new PathGeometry(new[] { river }), Stroke = new SolidColorBrush(Color.FromArgb(80, RiverC.R, RiverC.G, RiverC.B)),
            StrokeThickness = S(0.055), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false
        });

        // линии: верх (левый и верхний край), низ (нижний и правый край), центр (диагональ)
        Brush lane = Ui.Res("LineStrong");
        void Lane(Point a, Point b) => canvas.Children.Add(new Line
        {
            X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = lane, StrokeThickness = Math.Max(2.5, size / 110), IsHitTestVisible = false,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
        Lane(P(0.08, 0.14), P(0.08, 0.92)); Lane(P(0.08, 0.92), P(0.86, 0.92));
        Lane(P(0.14, 0.08), P(0.92, 0.08)); Lane(P(0.92, 0.08), P(0.92, 0.86));
        Lane(P(0.16, 0.16), P(0.84, 0.84));

        // базы и древние
        canvas.Children.Add(Base(0, S(1 - MapZones.BaseSize), S(MapZones.BaseSize), RadiantC));
        canvas.Children.Add(Base(S(1 - MapZones.BaseSize), 0, S(MapZones.BaseSize), DireC));
        canvas.Children.Add(Marker(P(0.09, 0.09), Math.Max(9, size / 26), RadiantC, true, Loc.T("map_ancient") + " · Radiant"));
        canvas.Children.Add(Marker(P(0.91, 0.91), Math.Max(9, size / 26), DireC, true, Loc.T("map_ancient") + " · Dire"));

        // вышки: по три на линию у каждой стороны (схематичные места)
        double t = Math.Max(5, size / 52);
        foreach (var (x, y) in new[]
                 {
                     (0.08, 0.62), (0.08, 0.44), (0.08, 0.26),
                     (0.36, 0.36), (0.28, 0.28), (0.21, 0.21),
                     (0.80, 0.08), (0.52, 0.08), (0.30, 0.08),
                 })
        {
            canvas.Children.Add(Marker(P(x, y), t, RadiantC, false, Loc.T("map_tower") + " · Radiant"));
            canvas.Children.Add(Marker(P(1 - x, 1 - y), t, DireC, false, Loc.T("map_tower") + " · Dire"));
        }

        // логова Рошана
        foreach (var (x, y) in new[] { (0.36, 0.64), (0.64, 0.36) })
            canvas.Children.Add(Ring(P(x, y), Math.Max(10, size / 22), Loc.T("map_roshan")));

        // лагеря леса (треугольники), зеркально для двух сторон
        double camp = Math.Max(6, size / 48);
        foreach (var (x, y) in new[] { (0.27, 0.36), (0.36, 0.24), (0.46, 0.17), (0.58, 0.16), (0.70, 0.17), (0.20, 0.50), (0.26, 0.58), (0.44, 0.31) })
        {
            canvas.Children.Add(Camp(P(x, y), camp, RadiantC, Loc.T("map_camp")));
            canvas.Children.Add(Camp(P(1 - x, 1 - y), camp, DireC, Loc.T("map_camp")));
        }

        // аванпосты — по одному на стороне, ближе к своей лёгкой линии
        foreach (var (x, y, c) in new[] { (0.62, 0.24, RadiantC), (0.38, 0.76, DireC) })
            canvas.Children.Add(Outpost(P(x, y), Math.Max(10, size / 30), c, Loc.T("map_outpost")));

        // руны: силы — у реки, богатства — в лесу
        foreach (var (x, y) in new[] { (0.30, 0.71), (0.70, 0.29) })
            canvas.Children.Add(Rune(P(x, y), Math.Max(8, size / 38), Color.FromRgb(79, 163, 247), Loc.T("map_power_rune")));
        foreach (var (x, y) in new[] { (0.30, 0.44), (0.70, 0.56), (0.46, 0.06), (0.54, 0.94) })
            canvas.Children.Add(Rune(P(x, y), Math.Max(7, size / 44), Color.FromRgb(242, 194, 75), Loc.T("map_bounty_rune")));

        // Торментеры — по одному в углах у лёгких линий противников
        foreach (var (x, y) in new[] { (0.80, 0.24), (0.20, 0.76) })
            canvas.Children.Add(Ring(P(x, y), Math.Max(9, size / 30), Loc.T("map_tormentor"), Color.FromRgb(255, 150, 80)));

        if (labels)
        {
            canvas.Children.Add(Label("TOP", P(0.12, 0.985), size));
            canvas.Children.Add(Label("MID", P(0.53, 0.47), size));
            canvas.Children.Add(Label("BOT", P(0.80, 0.135), size));
        }

        var list = points.ToList();
        // тепловой слой: мягкие пятна, которые складываются там, где смертей много
        if (heat && list.Count > 0)
        {
            double hr = S(0.10);
            foreach (var p in list)
            {
                var c = (p.Fill as SolidColorBrush)?.Color ?? Colors.OrangeRed;
                var spot = new Ellipse
                {
                    Width = hr * 2, Height = hr * 2, IsHitTestVisible = false,
                    Fill = new RadialGradientBrush(Color.FromArgb(80, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B))
                };
                Canvas.SetLeft(spot, S(p.X) - hr);
                Canvas.SetTop(spot, S(1 - p.Y) - hr);
                canvas.Children.Add(spot);
            }
        }

        double r = Math.Max(6, size / 40);
        foreach (var p in list)
        {
            var dot = new Ellipse
            {
                Width = r, Height = r, Fill = p.Fill, Opacity = 0.95,
                Stroke = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), StrokeThickness = 1,
                ToolTip = p.Tip
            };
            ToolTipService.SetInitialShowDelay(dot, 100);
            Canvas.SetLeft(dot, S(p.X) - r / 2);
            Canvas.SetTop(dot, S(1 - p.Y) - r / 2);
            canvas.Children.Add(dot);
        }
        return new Border
        {
            Width = size + 2, Height = size + 2, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            BorderBrush = Ui.Res("Line"), Background = Ui.Res("Panel2"), Child = canvas, ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left
        };
    }

    private static Polygon Poly(IEnumerable<Point> pts, Color fill) => new()
    {
        Points = new PointCollection(pts), Fill = new SolidColorBrush(fill), IsHitTestVisible = false
    };

    private static Border Patch(double left, double top, double w, double h, Color c)
    {
        var b = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(Math.Min(w, h) / 2), IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(22, c.R, c.G, c.B))
        };
        Canvas.SetLeft(b, left);
        Canvas.SetTop(b, top);
        return b;
    }

    private static Border Base(double left, double top, double size, Color c)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(6), IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B)), BorderThickness = new Thickness(1)
        };
        Canvas.SetLeft(b, left);
        Canvas.SetTop(b, top);
        return b;
    }

    /// <summary>Вышка (квадрат) или древний (ромб покрупнее).</summary>
    private static FrameworkElement Marker(Point at, double size, Color c, bool ancient, string tip)
    {
        var rect = new Rectangle
        {
            Width = size, Height = size, RadiusX = ancient ? 2 : 1.5, RadiusY = ancient ? 2 : 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(ancient ? (byte)200 : (byte)150, c.R, c.G, c.B)),
            Stroke = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), StrokeThickness = 1, ToolTip = tip,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = ancient ? new RotateTransform(45) : Transform.Identity
        };
        Canvas.SetLeft(rect, at.X - size / 2);
        Canvas.SetTop(rect, at.Y - size / 2);
        return rect;
    }

    private static FrameworkElement Ring(Point at, double size, string tip, Color? color = null)
    {
        var c = color ?? Color.FromRgb(201, 160, 255);
        var e = new Ellipse
        {
            Width = size, Height = size, Stroke = new SolidColorBrush(Color.FromArgb(170, c.R, c.G, c.B)), StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 2, 2 }, Fill = new SolidColorBrush(Color.FromArgb(30, c.R, c.G, c.B)), ToolTip = tip
        };
        Canvas.SetLeft(e, at.X - size / 2);
        Canvas.SetTop(e, at.Y - size / 2);
        return e;
    }

    /// <summary>Лагерь леса — маленький треугольник в цвете стороны.</summary>
    private static FrameworkElement Camp(Point at, double size, Color c, string tip)
    {
        var tri = new Polygon
        {
            Points = new PointCollection { new(size / 2, 0), new(size, size), new(0, size) },
            Fill = new SolidColorBrush(Color.FromArgb(110, c.R, c.G, c.B)), Stroke = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            StrokeThickness = 0.8, ToolTip = tip
        };
        Canvas.SetLeft(tri, at.X - size / 2);
        Canvas.SetTop(tri, at.Y - size / 2);
        return tri;
    }

    /// <summary>Аванпост — кольцо с точкой.</summary>
    private static FrameworkElement Outpost(Point at, double size, Color c, string tip)
    {
        var g = new Grid { Width = size, Height = size, ToolTip = tip };
        g.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(200, c.R, c.G, c.B)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B)) });
        g.Children.Add(new Ellipse { Width = size / 3, Height = size / 3, Fill = new SolidColorBrush(c) });
        Canvas.SetLeft(g, at.X - size / 2);
        Canvas.SetTop(g, at.Y - size / 2);
        return g;
    }

    /// <summary>Руна — ромбик.</summary>
    private static FrameworkElement Rune(Point at, double size, Color c, string tip)
    {
        var r = new Rectangle
        {
            Width = size, Height = size, Fill = new SolidColorBrush(Color.FromArgb(200, c.R, c.G, c.B)),
            Stroke = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), StrokeThickness = 0.8, ToolTip = tip,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45)
        };
        Canvas.SetLeft(r, at.X - size / 2);
        Canvas.SetTop(r, at.Y - size / 2);
        return r;
    }

    private static TextBlock Label(string text, Point at, double size)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = Math.Max(9, size / 34), FontWeight = FontWeights.SemiBold, IsHitTestVisible = false,
            Foreground = Ui.Res("Faint")
        };
        Canvas.SetLeft(tb, at.X);
        Canvas.SetTop(tb, at.Y);
        return tb;
    }
}
