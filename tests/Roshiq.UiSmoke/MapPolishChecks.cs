using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Roshiq.App.Views;
using Roshiq.Core.Models;

internal static class MapPolishChecks
{
    internal static void Run(string[] args)
    {
        var points = new[] {
            new MiniMap.Point2(.22, .72, MiniMap.DeathPhaseBrush(GamePhase.Lane), "Example: lane"),
            new MiniMap.Point2(.52, .47, MiniMap.DeathPhaseBrush(GamePhase.Mid), "Example: mid game"),
            new MiniMap.Point2(.76, .20, MiniMap.DeathPhaseBrush(GamePhase.Late), "Example: late game") };
        Require(MiniMap.HasArt, "Detailed map art loads from the application assembly");
        var normal = MiniMap.Create(points, 340, heat: true, distinctMarkers: true);
        var dimmed = MiniMap.Create(points, 340, heat: true, distinctMarkers: true, dimBackground: true);
        var canvas = (Canvas)((Border)normal).Child;
        var rings = canvas.Children.OfType<Grid>().Where(g => points.Any(p => Equals(p.Tip, g.ToolTip))).ToList();
        Require(rings.Count == points.Length, "Recorded deaths have distinct ring markers");
        for (int i = 0; i < rings.Count; i++)
        {
            var ring = rings[i];
            Require(ring.Children.Count == 2 && ring.Children[0] is Ellipse && ring.Children[1] is Ellipse { StrokeThickness: 1.5 }, "Double outline on each death");
            Require(Math.Abs(Canvas.GetLeft(ring) + ring.Width / 2 - points[i].X * 340) < .01 &&
                Math.Abs(Canvas.GetTop(ring) + ring.Height / 2 - (1 - points[i].Y) * 340) < .01, "Marker preserves recorded map coordinates");
            Require(Equals(ring.ToolTip, points[i].Tip), "Marker keeps its event tooltip");
        }
        Require(points.Select(p => ((SolidColorBrush)p.Fill).Color).Distinct().Count() == 3, "Game phases have distinct non-orange colours");
        if (MiniMap.HasArt)
        {
            var a = (Rectangle)canvas.Children[1];
            var b = (Rectangle)((Canvas)((Border)dimmed).Child).Children[1];
            Require(((SolidColorBrush)b.Fill).Color.A > ((SolidColorBrush)a.Fill).Color.A, "Dim mode changes background only");
        }
        var wardMap = (Canvas)((Border)MiniMap.Create(points)).Child;
        Require(!wardMap.Children.OfType<Grid>().Any(g => points.Any(p => Equals(p.Tip, g.ToolTip))), "Other map uses retain their original markers");
        int index = Array.IndexOf(args, "--map-preview");
        if (index >= 0 && index + 1 < args.Length)
        {
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Black };
            pair.Children.Add(normal); pair.Children.Add(dimmed);
            pair.Measure(new Size(684, 342)); pair.Arrange(new Rect(0, 0, 684, 342)); pair.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1026, 513, 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(pair);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(args[index + 1]); png.Save(output);
        }
    }
    private static void Require(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }
}
