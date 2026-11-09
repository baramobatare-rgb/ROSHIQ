using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Roshiq.Core.Diagnostics;

namespace Roshiq.App;

/// <summary>
/// Подмена цветов дизайн-системы на лету. Все стили используют DynamicResource,
/// поэтому смена темы сразу перекрашивает главное окно и оверлей.
/// </summary>
public static class ThemeManager
{
    public sealed record Palette(string Key, string NameRu, string NameEn, Color Accent, Color Hover, Color Soft, Color OnAccent, string Brand);

    /// <summary>Фон интерфейса: насколько тёмные фон, панели и линии. Акцент выбирается отдельно.</summary>
    public sealed record Backdrop(string Key, string NameRu, string NameEn, Color Bg, Color Panel, Color Panel2, Color Panel3, Color Line, Color LineStrong);

    public static readonly IReadOnlyList<Backdrop> Backdrops = new[]
    {
        new Backdrop("standard", "Стандартный", "Standard", Hex("#090B0A"), Hex("#121614"), Hex("#191E1B"), Hex("#222823"), Hex("#232925"), Hex("#303830")),
        new Backdrop("deep", "Глубокий", "Deep", Hex("#050606"), Hex("#0C0E0D"), Hex("#121513"), Hex("#1A1E1B"), Hex("#1B1F1C"), Hex("#272D28")),
        new Backdrop("black", "Чёрный (OLED)", "Black (OLED)", Hex("#000000"), Hex("#070808"), Hex("#0D0F0E"), Hex("#151816"), Hex("#161917"), Hex("#232724")),
    };

    public static readonly IReadOnlyList<Palette> Palettes = new[]
    {
        new Palette("lime", "Lime — неоновый зелёный", "Lime — neon green", Hex("#B5FF4D"), Hex("#C7FF7A"), Hex("#1C2612"), Hex("#0A0D07"), "green"),
        new Palette("crimson", "Crimson — красный", "Crimson — red", Hex("#FF4D5E"), Hex("#FF7381"), Hex("#2A1216"), Hex("#FFFFFF"), "red"),
        new Palette("violet", "Violet — фиолетовый", "Violet — purple", Hex("#A78BFF"), Hex("#BCA6FF"), Hex("#1D1830"), Hex("#0B0A12"), "violet"),
        new Palette("azure", "Azure — синий", "Azure — blue", Hex("#4DB8FF"), Hex("#7ACBFF"), Hex("#10202C"), Hex("#06101A"), "blue"),
        new Palette("teal", "Teal — бирюзовый", "Teal — turquoise", Hex("#3DF5D0"), Hex("#74F8DE"), Hex("#0F2622"), Hex("#04110E"), "teal"),
        new Palette("gold", "Gold — золотой", "Gold — golden", Hex("#FFC93D"), Hex("#FFD76E"), Hex("#2A2410"), Hex("#120E02"), "gold"),
        new Palette("orange", "Orange — оранжевый", "Orange — orange", Hex("#FF8A3D"), Hex("#FFA56B"), Hex("#2B1A0E"), Hex("#140A03"), "orange"),
    };

    public static string Current { get; private set; } = "lime";
    public static string CurrentBackdrop { get; private set; } = "standard";
    /// <summary>Цвет фона текущей темы — для заголовка окна Windows.</summary>
    public static Color CurrentBg { get; private set; } = Hex("#090B0A");
    /// <summary>Набор иконок текущей темы: green / red / violet.</summary>
    public static string CurrentBrand { get; private set; } = "green";
    /// <summary>Тема применена (для обновления значка в трее).</summary>
    public static event EventHandler? Applied;

    public static Uri IconUri(string brand) => BrandUri(brand, "app.ico");

    public static void Apply(string key)
    {
        var p = Palettes.FirstOrDefault(x => x.Key == key) ?? Palettes[0];
        Current = p.Key;
        var res = Application.Current.Resources;
        res["Accent"] = Brush(p.Accent);
        res["AccentHover"] = Brush(p.Hover);
        res["AccentSoft"] = Brush(p.Soft);
        res["OnAccent"] = Brush(p.OnAccent);
        // «Хорошо» всегда зелёное, а не цвет темы: в Crimson красный «успех» путается с ошибкой
        res["Success"] = Brush(p.Key == "lime" ? p.Accent : Hex("#7EE08A"));
        // В золотой теме «внимание» уходит в оранжевый, чтобы не сливаться с акцентом
        res["Warning"] = Brush(p.Key == "gold" ? Hex("#FF9E4D") : Hex("#FFC24D"));
        CurrentBrand = p.Brand;
        ApplyBrand(p.Brand);
        Applied?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Тема целиком: акцент и фон.</summary>
    public static void Apply(string key, string? backdrop)
    {
        ApplyBackdrop(backdrop);
        Apply(key);
    }

    public static void ApplyBackdrop(string? key)
    {
        var b = Backdrops.FirstOrDefault(x => x.Key == key) ?? Backdrops[0];
        CurrentBackdrop = b.Key;
        CurrentBg = b.Bg;
        var res = Application.Current.Resources;
        res["Bg"] = Brush(b.Bg);
        res["Panel"] = Brush(b.Panel);
        res["Panel2"] = Brush(b.Panel2);
        res["Panel3"] = Brush(b.Panel3);
        res["Line"] = Brush(b.Line);
        res["LineStrong"] = Brush(b.LineStrong);
        Applied?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Иконки ROSHIQ под цвет темы: AppIcon — иконка окон и панели задач, AppLogo — логотип в шапке,
    /// AppMark — маленький знак в оверлее и уведомлениях. Иконка самого .exe встроена при сборке (зелёная)
    /// и от темы не зависит — так устроена Windows.
    /// </summary>
    private static void ApplyBrand(string brand)
    {
        var res = Application.Current.Resources;
        var logo = LoadPng(brand, "logo.png");
        var mark = LoadPng(brand, "mark.png");
        if (logo != null) res["AppLogo"] = logo;
        if (mark != null) res["AppMark"] = mark;

        ImageSource? icon = null;
        try
        {
            var frame = BitmapFrame.Create(BrandUri(brand, "app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            icon = frame;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось загрузить иконку темы " + brand, ex);
            icon = logo;
        }
        if (icon == null) return;
        res["AppIcon"] = icon;
        foreach (Window w in Application.Current.Windows)
            if (w.ShowInTaskbar) w.Icon = icon;
    }

    /// <summary>Логотип конкретной темы — для превью в настройках.</summary>
    public static ImageSource? LogoFor(Palette p) => LoadPng(p.Brand, "logo.png");

    private static ImageSource? LoadPng(string brand, string file)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = BrandUri(brand, file);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось загрузить " + brand + "/" + file, ex);
            return null;
        }
    }

    private static Uri BrandUri(string brand, string file) =>
        new($"pack://application:,,,/Roshiq;component/Assets/Brand/{brand}/{file}", UriKind.Absolute);

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);
}
