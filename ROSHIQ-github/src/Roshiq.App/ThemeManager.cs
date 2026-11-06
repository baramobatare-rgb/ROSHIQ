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

    public static readonly IReadOnlyList<Palette> Palettes = new[]
    {
        new Palette("lime", "Lime — неоновый зелёный", "Lime — neon green", Hex("#B5FF4D"), Hex("#C7FF7A"), Hex("#1C2612"), Hex("#0A0D07"), "green"),
        new Palette("crimson", "Crimson — красный", "Crimson — red", Hex("#FF4D5E"), Hex("#FF7381"), Hex("#2A1216"), Hex("#FFFFFF"), "red"),
        new Palette("violet", "Violet — фиолетовый", "Violet — purple", Hex("#A78BFF"), Hex("#BCA6FF"), Hex("#1D1830"), Hex("#0B0A12"), "violet"),
    };

    public static string Current { get; private set; } = "lime";
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
        res["Success"] = Brush(p.Accent);
        CurrentBrand = p.Brand;
        ApplyBrand(p.Brand);
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
