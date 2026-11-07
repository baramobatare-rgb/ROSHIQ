using System.IO.Compression;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Settings;

namespace Roshiq.App.Services;

/// <summary>
/// Картинки для интерфейса. Портреты интерфейса берутся из Assets/HeroArt.zip в исходном разрешении Valve.
/// Шаблоны распознавания 96×54 используются только как резерв. Иконки предметов — из встроенного Assets/ItemIcons.zip (его собирает
/// tools/fetch-item-icons.ps1 при сборке). Если какой-то иконки там нет, она скачивается с CDN Valve и кэшируется в
/// %AppData%\Roshiq\cache\items — только если в настройках разрешена загрузка из сети.
/// </summary>
public static class HeroArt
{
    private const string ItemCdn = "https://cdn.akamai.steamstatic.com/apps/dota2/images/dota_react/items/";

    private static readonly object Sync = new();
    private static Dictionary<string, ImageSource>? _builtinItems;
    private static readonly Dictionary<string, ImageSource?> Items = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Downloads = new(4);
    private static HttpClient? _http;
    private static bool _dirty;

    /// <summary>Скачались новые иконки предметов — экранам стоит перерисоваться.</summary>
    public static event EventHandler? ItemIconsUpdated;

    private static string ItemDir => Path.Combine(AppPaths.Cache, "items");

    /// <summary>Портрет героя по ключу (antimage, axe…) или null. Картинки разбираются по одной — при первом обращении к герою.</summary>
    public static ImageSource? Portrait(string? heroKey)
    {
        if (string.IsNullOrEmpty(heroKey)) return null;
        lock (Sync)
        {
            if (Portraits.TryGetValue(heroKey, out var cached)) return cached;
            OpenArchives();
            var img = LoadArt(heroKey) ?? LoadTemplate(heroKey);
            Portraits[heroKey] = img;
            return img;
        }
    }

    private static readonly Dictionary<string, ImageSource?> Portraits = new(StringComparer.OrdinalIgnoreCase);
    private static ZipArchive? _art, _templates;
    private static bool _archivesOpened;

    /// <summary>Открывает встроенные архивы один раз (вызывается под Sync). Отсутствие любого из них — не ошибка.</summary>
    private static void OpenArchives()
    {
        if (_archivesOpened) return;
        _archivesOpened = true;
        _art = OpenResource("Roshiq.App.Assets.HeroArt.zip");
        _templates = OpenResource("Roshiq.App.Assets.Portraits.zip");
    }

    private static ZipArchive? OpenResource(string name)
    {
        try
        {
            var resource = typeof(HeroArt).Assembly.GetManifestResourceStream(name);
            return resource == null ? null : new ZipArchive(resource, ZipArchiveMode.Read);
        }
        catch (Exception ex) { Log.Warn($"Архив {name} не открыт: {ex.Message}"); return null; }
    }

    /// <summary>Портрет в исходном разрешении Valve из HeroArt.zip.</summary>
    private static ImageSource? LoadArt(string key)
    {
        var entry = _art?.GetEntry(key + ".png");
        if (entry == null) return null;
        try
        {
            using var stream = new MemoryStream();
            using (var source = entry.Open()) source.CopyTo(stream);
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) { Log.Warn($"Портрет {key} повреждён: {ex.Message}"); return null; }
    }

    /// <summary>Запасной вариант — шаблон распознавания 96×54 из Portraits.zip.</summary>
    private static ImageSource? LoadTemplate(string key)
    {
        var entry = _templates?.GetEntry(key + "/base.dpt");
        if (entry == null) return null;
        try
        {
            using var stream = entry.Open();
            var image = Roshiq.Vision.RgbImage.Load(stream);
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Rgb24, null, image.Data, image.Width * 3);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) { Log.Warn($"Шаблон портрета {key} не загружен: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Иконка предмета (ключ item_xxx) или null. Если иконки ещё нет и сеть разрешена — запускается загрузка,
    /// по окончании приходит событие <see cref="ItemIconsUpdated"/>.
    /// </summary>
    public static ImageSource? ItemIcon(string? itemKey)
    {
        if (string.IsNullOrEmpty(itemKey)) return null;
        var name = itemKey.StartsWith("item_", StringComparison.OrdinalIgnoreCase) ? itemKey[5..] : itemKey;
        // у рецептов своих иконок на CDN нет (404) — показываем общий свиток рецепта
        if (name.StartsWith("recipe_", StringComparison.OrdinalIgnoreCase)) name = "recipe";
        if (name.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) return null;
        lock (Sync)
        {
            _builtinItems ??= LoadBuiltinItems();
            if (_builtinItems.TryGetValue(name, out var builtin)) return builtin;
            if (Items.TryGetValue(name, out var cached)) return cached;
        }
        var path = Path.Combine(ItemDir, name + ".png");
        if (File.Exists(path))
        {
            var img = LoadFile(path);
            lock (Sync) Items[name] = img;
            return img;
        }
        StartDownload(name, path);
        return null;
    }

    private static Dictionary<string, ImageSource> LoadBuiltinItems()
    {
        var result = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var resource = typeof(HeroArt).Assembly.GetManifestResourceStream("Roshiq.App.Assets.ItemIcons.zip");
            if (resource == null) return result;
            using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (!entry.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var ms = new MemoryStream();
                    using (var zs = entry.Open()) zs.CopyTo(ms);
                    ms.Position = 0;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    // Preserve native pixels for high-DPI displays.
                    bmp.EndInit();
                    bmp.Freeze();
                    result[Path.GetFileNameWithoutExtension(entry.Name)] = bmp;
                }
                catch (Exception ex) { Log.Warn($"Встроенная иконка {entry.Name} повреждена: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn("Встроенные иконки предметов не загружены: " + ex.Message); }
        return result;
    }

    private static ImageSource? LoadFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            // Preserve native pixels for high-DPI displays.
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Иконка {Path.GetFileName(path)} повреждена: {ex.Message}");
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    private static void StartDownload(string name, string path)
    {
        if (App.Host is not { } host || !host.Settings.Current.Data.AllowOnlineStats) return;
        lock (Sync)
        {
            if (Failed.Contains(name) || !Pending.Add(name)) return;
        }
        _ = Task.Run(async () =>
        {
            await Downloads.WaitAsync().ConfigureAwait(false);
            bool ok = false;
            try
            {
                _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var bytes = await _http.GetByteArrayAsync(ItemCdn + name + ".png").ConfigureAwait(false);
                // PNG начинается с сигнатуры 89 50 4E 47 — не сохраняем страницы ошибок
                if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                {
                    Directory.CreateDirectory(ItemDir);
                    await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
                    ok = true;
                }
            }
            catch (Exception ex) { Log.Warn($"Иконка {name}: {ex.Message}"); }
            finally
            {
                Downloads.Release();
                lock (Sync)
                {
                    Pending.Remove(name);
                    if (!ok) Failed.Add(name);
                    else { Items.Remove(name); _dirty = true; }
                    if (_dirty && Pending.Count == 0)
                    {
                        _dirty = false;
                        RaiseUpdated();
                    }
                }
            }
        });
    }

    private static void RaiseUpdated()
    {
        var app = System.Windows.Application.Current;
        app?.Dispatcher.InvokeAsync(() => ItemIconsUpdated?.Invoke(null, EventArgs.Empty));
    }
}
