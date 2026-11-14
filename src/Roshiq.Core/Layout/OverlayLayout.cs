using Roshiq.Core.Models;

namespace Roshiq.Core.Layout;

/// <summary>Прямоугольник в долях (0..1) от клиентской области окна игры — не зависит от разрешения.</summary>
public sealed record RelRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;

    public bool Intersects(RelRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public PixelRect ToPixels(PixelRect host) => new(
        host.X + (int)Math.Round(X * host.Width),
        host.Y + (int)Math.Round(Y * host.Height),
        Math.Max(1, (int)Math.Round(W * host.Width)),
        Math.Max(1, (int)Math.Round(H * host.Height)));

    public static RelRect FromPixels(PixelRect rect, PixelRect host)
    {
        if (host.Width <= 0 || host.Height <= 0) return new RelRect(0, 0, 0.2, 0.2);
        return new RelRect(
            (rect.X - host.X) / (double)host.Width,
            (rect.Y - host.Y) / (double)host.Height,
            rect.Width / (double)host.Width,
            rect.Height / (double)host.Height);
    }
}

/// <summary>Прямоугольник в физических пикселях экрана.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Зоны интерфейса Dota 2, которые оверлей не должен перекрывать (в долях экрана 16:9, стандартный HUD).
/// Мини-карта — слева снизу, верхняя панель с портретами и часами — по центру сверху,
/// панель способностей/предметов — снизу по центру-справа.
/// </summary>
public static class SafeZone
{
    public static readonly RelRect Minimap = new(0.0, 0.70, 0.16, 0.30);
    public static readonly RelRect TopBar = new(0.27, 0.0, 0.46, 0.075);
    public static readonly RelRect BottomHud = new(0.25, 0.80, 0.55, 0.20);
    public static readonly IReadOnlyList<RelRect> Forbidden = new[] { Minimap, TopBar, BottomHud };

    /// <summary>Ограничить размер, удержать в окне и вытолкнуть из запретных зон.</summary>
    public static RelRect Clamp(RelRect r)
    {
        double w = Math.Clamp(Finite(r.W, 0.19), 0.08, 0.45);
        double h = Math.Clamp(Finite(r.H, 0.30), 0.05, 0.70);
        double x = Math.Clamp(Finite(r.X, 0.78), 0.0, 1.0 - w);
        double y = Math.Clamp(Finite(r.Y, 0.22), 0.0, 1.0 - h);
        var rect = new RelRect(x, y, w, h);
        if (!Forbidden.Any(rect.Intersects)) return rect;

        // Ищем ближайшее положение того же размера, не пересекающее ни одну запретную зону.
        var xs = new HashSet<double> { x, 0, 1 - w };
        var ys = new HashSet<double> { y, 0, 1 - h };
        foreach (var z in Forbidden)
        {
            xs.Add(z.Right); xs.Add(z.X - w);
            ys.Add(z.Bottom); ys.Add(z.Y - h);
        }
        RelRect? best = null;
        double bestDist = double.MaxValue;
        foreach (var cx in xs)
            foreach (var cy in ys)
            {
                if (cx < 0 || cy < 0 || cx + w > 1 + 1e-9 || cy + h > 1 + 1e-9) continue;
                var c = new RelRect(cx, cy, w, h);
                if (Forbidden.Any(c.Intersects)) continue;
                double d = Math.Abs(cx - x) + Math.Abs(cy - y);
                if (d < bestDist) { bestDist = d; best = c; }
            }
        return best ?? new RelRect(1.0 - w, 0.22, w, Math.Min(h, 0.5));
    }

    /// <summary>Позиция всплывающего напоминания: правая часть экрана, под верхней панелью, вне мини-карты.</summary>
    public static RelRect ToastArea => new(0.74, 0.09, 0.24, 0.07);

    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;
}

/// <summary>
/// 0.24: где показать карточку-совет относительно виджета. Над виджетом той же ширины; если сверху не помещается
/// (мешает верхняя панель игры) — под видимой частью виджета; если и там нет места — у верхнего края, вне верхней панели.
/// </summary>
public static class CardPlacement
{
    public static PixelRect Place(PixelRect widget, PixelRect host, int cardHeight, int gap)
    {
        cardHeight = Math.Max(1, cardHeight);
        int width = Math.Max(1, widget.Width);
        int x = Math.Clamp(widget.X, host.X, Math.Max(host.X, host.X + host.Width - width));
        int topLimit = host.Y + (int)Math.Round(host.Height * SafeZone.TopBar.Bottom);
        int above = widget.Y - gap - cardHeight;
        if (above >= topLimit) return new PixelRect(x, above, width, cardHeight);
        int below = widget.Y + widget.Height + gap;
        if (below + cardHeight <= host.Y + host.Height) return new PixelRect(x, below, width, cardHeight);
        return new PixelRect(x, Math.Max(host.Y, topLimit), width, cardHeight);
    }
}

/// <summary>
/// Автомасштаб оверлея под экран: текст и значки занимают ту же долю высоты, что на 1080p.
/// Считается по высоте окна игры в логических пикселях, поэтому масштаб Windows (125%, 150%) учитывается и не удваивается:
/// 3440×1440 при 100% → ×1,35; 2560×1440 при 125% → ×1,05; 3840×2160 при 150% → ×1,35. Меньше 1080 не уменьшаем.
/// </summary>
public static class ScreenScale
{
    public const double BaseHeight = 1080;
    public const double Max = 2.0;

    public static double Auto(int heightPx, double dpiScale)
    {
        if (heightPx <= 0 || !(dpiScale > 0) || !double.IsFinite(dpiScale)) return 1.0;
        var v = Math.Clamp(heightPx / dpiScale / BaseHeight, 1.0, Max);
        return Math.Round(v * 20) / 20;
    }
}

/// <summary>Состояние окна игры, получаемое от трекера окна.</summary>
public sealed record GameWindowState(bool Found, bool Minimized, bool Foreground, PixelRect ClientArea)
{
    public static readonly GameWindowState NotFound = new(false, false, false, default);
}

/// <summary>Чистая логика: показывать ли оверлей и где (тестируется без Windows и без Dota).</summary>
public static class OverlayVisibility
{
    public sealed record Decision(bool Visible, PixelRect Bounds, string ReasonKey);

    public static Decision Decide(GameWindowState game, OverlayMode mode, RelRect panel,
        bool userHidden, bool hideWhenNotFocused, bool testMode, PixelRect primaryScreen, bool overlayFocused = false)
    {
        if (mode == OverlayMode.Hidden || userHidden)
            return new Decision(false, default, "overlay_hidden_by_user");

        PixelRect host;
        if (game.Found)
        {
            if (game.Minimized) return new Decision(false, default, "overlay_game_minimized");
            if (game.ClientArea.IsEmpty) return new Decision(false, default, "overlay_game_minimized");
            if (hideWhenNotFocused && !game.Foreground && !overlayFocused)
                return new Decision(false, default, "overlay_game_unfocused");
            host = game.ClientArea;
        }
        else if (testMode)
        {
            host = primaryScreen;
        }
        else
        {
            return new Decision(false, default, "overlay_game_not_found");
        }

        var bounds = SafeZone.Clamp(panel).ToPixels(host);
        if (mode == OverlayMode.Minimal)
        {
            // компактный виджет (0.24): главная строка, золото и до трёх строк «Скоро».
            // Окно с запасом по высоте, сама карточка — по содержимому (ниже окно прозрачное и не ловит мышь).
            bounds = bounds with { Height = Math.Min(Math.Max(220, (int)Math.Round(host.Height * 0.24)), Math.Max(104, host.Y + host.Height - bounds.Y)) };
        }
        return new Decision(true, bounds, game.Found ? "overlay_attached" : "overlay_test_mode");
    }
}
