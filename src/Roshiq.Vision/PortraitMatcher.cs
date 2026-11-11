using Roshiq.Core.Diagnostics;

namespace Roshiq.Vision;

/// <summary>Вектор признаков портрета: нормированная уменьшенная картинка + цветовая гистограмма.</summary>
public sealed class PortraitFeature
{
    public const int W = 24, H = 14, Bins = 8;

    public float[] Pixels { get; }
    public float[] Histogram { get; }

    private PortraitFeature(float[] pixels, float[] hist) { Pixels = pixels; Histogram = hist; }

    public static PortraitFeature From(RgbImage img)
    {
        var small = img.ResizeArea(W, H);
        var px = new float[W * H * 3];
        double mean = 0;
        for (int i = 0; i < px.Length; i++) { px[i] = small.Data[i]; mean += px[i]; }
        mean /= px.Length;
        double norm = 0;
        for (int i = 0; i < px.Length; i++) { px[i] -= (float)mean; norm += px[i] * px[i]; }
        norm = Math.Sqrt(norm);
        if (norm > 1e-6) for (int i = 0; i < px.Length; i++) px[i] /= (float)norm;

        var hist = new float[Bins * 3];
        int n = small.Width * small.Height;
        for (int i = 0; i < small.Data.Length; i += 3)
        {
            hist[small.Data[i] * Bins / 256]++;
            hist[Bins + small.Data[i + 1] * Bins / 256]++;
            hist[2 * Bins + small.Data[i + 2] * Bins / 256]++;
        }
        for (int i = 0; i < hist.Length; i++) hist[i] /= n;
        return new PortraitFeature(px, hist);
    }

    /// <summary>Сходство 0..1: 70% — нормированная корреляция, 30% — пересечение гистограмм.</summary>
    public double Similarity(PortraitFeature other)
    {
        double dot = 0;
        for (int i = 0; i < Pixels.Length; i++) dot += Pixels[i] * other.Pixels[i];
        double ncc = (Math.Clamp(dot, -1, 1) + 1) / 2;
        double inter = 0;
        for (int i = 0; i < Histogram.Length; i++) inter += Math.Min(Histogram[i], other.Histogram[i]);
        inter /= 3; // три канала
        return 0.7 * ncc + 0.3 * inter;
    }
}

public enum SlotStatus { Empty, Recognized, Ambiguous, Unknown }

public sealed record MatchCandidate(string HeroKey, double Score);

public sealed record SlotMatch(SlotStatus Status, string? HeroKey, double Score, IReadOnlyList<MatchCandidate> Candidates);

/// <summary>
/// Признаки слота драфта (0.19.2). В верхней панели выбора поверх портрета лежат цветная полоса игрока (сверху) и плашка ранга
/// (нижние ~40%), сам портрет обрезан и наклонён. Поэтому сравнивается только середина слота над плашкой ранга, а для
/// встроенных портретов героев перебираются окна разного масштаба и сдвига — какое лучше совпадёт с тем, что видно в игре.
/// </summary>
public static class SlotFeature
{
    public const int W = 20, H = 8;
    /// <summary>Видимая часть портрета в слоте: без полосы цвета игрока и плашки ранга. Доли ширины и высоты слота.</summary>
    public const double X0 = 0.15, Y0 = 0.10, X1 = 0.85, Y1 = 0.55;
    // грубая сетка окон — для всех героев заранее; точная — только для пяти лучших кандидатов слота
    private static readonly double[] Scales = { 0.55, 0.65, 0.75, 0.85, 0.95 };
    private static readonly double[] ShiftsX = { 0, 0.25, 0.5, 0.75, 1 };
    private static readonly double[] ShiftsY = { 0, 0.2, 0.4, 0.6 };
    private static readonly double[] FineScales = Enumerable.Range(0, 11).Select(i => 0.5 + 0.05 * i).ToArray();
    private static readonly double[] FineShiftsX = Enumerable.Range(0, 9).Select(i => i / 8.0).ToArray();
    private static readonly double[] FineShiftsY = Enumerable.Range(0, 7).Select(i => i / 6.0).ToArray();

    /// <summary>Отношение сторон видимой части для слота стандартного размера (доли из настроек 16:9).</summary>
    public static double Aspect(int slotW, int slotH) => (X1 - X0) * slotW / ((Y1 - Y0) * slotH);

    /// <summary>Вектор видимой части слота.</summary>
    public static float[] OfSlot(RgbImage slot)
    {
        int x0 = (int)(X0 * slot.Width), y0 = (int)(Y0 * slot.Height);
        int w = Math.Max(1, (int)(X1 * slot.Width) - x0), h = Math.Max(1, (int)(Y1 * slot.Height) - y0);
        return Vector(slot.Crop(x0, y0, w, h));
    }

    /// <summary>Окна встроенного портрета героя: разные масштаб и сдвиг с тем же соотношением сторон, что у видимой части слота.</summary>
    public static IEnumerable<float[]> OfArt(RgbImage art, double aspect, bool fine = false)
    {
        foreach (var s in fine ? FineScales : Scales)
        {
            double ww = s * art.Width, hh = ww / aspect;
            if (hh > art.Height) continue;
            foreach (var fx in fine ? FineShiftsX : ShiftsX)
                foreach (var fy in fine ? FineShiftsY : ShiftsY)
                {
                    int x0 = (int)((art.Width - ww) * fx), y0 = (int)((art.Height - hh) * fy);
                    yield return Vector(art.Crop(x0, y0, Math.Max(1, (int)ww), Math.Max(1, (int)hh)));
                }
        }
    }

    /// <summary>Уменьшенная картинка без средней яркости, нормированная: скалярное произведение двух векторов — корреляция −1…1.</summary>
    public static float[] Vector(RgbImage img)
    {
        var small = img.ResizeArea(W, H);
        var v = new float[W * H * 3];
        double mean = 0;
        for (int i = 0; i < v.Length; i++) { v[i] = small.Data[i]; mean += v[i]; }
        mean /= v.Length;
        double norm = 0;
        for (int i = 0; i < v.Length; i++) { v[i] -= (float)mean; norm += v[i] * v[i]; }
        norm = Math.Sqrt(norm);
        if (norm > 1e-6) for (int i = 0; i < v.Length; i++) v[i] /= (float)norm;
        return v;
    }

    public static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}

/// <summary>
/// Сопоставление портрета с локальными шаблонами. Шаблоны создаются самим пользователем
/// (кнопка "Запомнить" при ручном исправлении) — никаких игровых файлов не читается.
/// </summary>
public sealed class PortraitMatcher
{
    private readonly Dictionary<string, List<PortraitFeature>> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    // ---- слоты драфта: встроенные портреты (окна считаются при первом распознавании) и слоты, запомненные пользователем ----
    private readonly Dictionary<string, List<RgbImage>> _art = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<float[]>> _slotVectors = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, float[][]>? _artWindows;
    private double _artAspect;

    /// <summary>Порог уверенного совпадения слота: высокий балл с заметным отрывом или средний балл с большим отрывом.</summary>
    public const double SlotSure = 0.80, SlotSureMargin = 0.10, SlotMaybe = 0.60;

    public double Threshold { get; set; } = 0.82;
    public double AmbiguityMargin { get; set; } = 0.025;
    public double EmptyStdDev { get; set; } = 6.0;

    public int TemplateCount { get { lock (_sync) return _templates.Values.Sum(v => v.Count); } }
    public int HeroCount { get { lock (_sync) return _templates.Count; } }

    public void AddTemplate(string heroKey, RgbImage image)
    {
        var f = PortraitFeature.From(image);
        lock (_sync)
        {
            if (!_templates.TryGetValue(heroKey, out var list)) _templates[heroKey] = list = new();
            list.Add(f);
        }
        AddSlotTemplate(heroKey, image);
    }

    public void Clear()
    {
        lock (_sync) { _templates.Clear(); _art.Clear(); _slotVectors.Clear(); _artWindows = null; }
    }

    /// <summary>Встроенный портрет героя (полная картинка, не слот из игры).</summary>
    public void AddArtTemplate(string heroKey, RgbImage art)
    {
        lock (_sync)
        {
            if (!_art.TryGetValue(heroKey, out var list)) _art[heroKey] = list = new();
            list.Add(art);
            _artWindows = null;
        }
    }

    /// <summary>Слот драфта, сохранённый как есть (ручное исправление «Запомнить»).</summary>
    public void AddSlotTemplate(string heroKey, RgbImage slot)
    {
        var v = SlotFeature.OfSlot(slot);
        lock (_sync)
        {
            if (!_slotVectors.TryGetValue(heroKey, out var list)) _slotVectors[heroKey] = list = new();
            list.Add(v);
        }
    }

    /// <summary>
    /// Распознать слот верхней панели драфта: сравнить видимую часть портрета с окнами встроенных портретов и с запомненными
    /// слотами. Плашка ранга и цвет игрока на результат не влияют.
    /// </summary>
    public SlotMatch MatchSlot(RgbImage slot)
    {
        if (slot.LumaStdDev() < EmptyStdDev)
            return new SlotMatch(SlotStatus.Empty, null, 0, Array.Empty<MatchCandidate>());
        var v = SlotFeature.OfSlot(slot);
        Dictionary<string, float[][]> art;
        Dictionary<string, RgbImage[]> artImages;
        List<KeyValuePair<string, List<float[]>>> taught;
        lock (_sync)
        {
            double aspect = SlotFeature.Aspect(slot.Width, slot.Height);
            if (_artWindows == null || Math.Abs(aspect - _artAspect) > 0.05)
            {
                _artAspect = aspect;
                _artWindows = _art.ToDictionary(kv => kv.Key, kv => kv.Value.SelectMany(a => SlotFeature.OfArt(a, aspect)).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            }
            art = _artWindows;
            artImages = _art.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            taught = _slotVectors.ToList();
        }
        var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, windows) in art)
            foreach (var w in windows)
            {
                double s = SlotFeature.Dot(v, w);
                if (!best.TryGetValue(key, out var b) || s > b) best[key] = s;
            }
        foreach (var (key, list) in taught)
            foreach (var w in list)
            {
                double s = SlotFeature.Dot(v, w);
                if (!best.TryGetValue(key, out var b) || s > b) best[key] = s;
            }
        // уточнение: для пяти лучших героев — точная сетка окон, она находит то же кадрирование, что в игре
        double aspectNow = SlotFeature.Aspect(slot.Width, slot.Height);
        foreach (var key in best.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key).ToList())
            if (artImages.TryGetValue(key, out var images))
                foreach (var w in images.SelectMany(a => SlotFeature.OfArt(a, aspectNow, fine: true)))
                {
                    double s = SlotFeature.Dot(v, w);
                    if (s > best[key]) best[key] = s;
                }
        var ranked = best.Select(kv => new MatchCandidate(kv.Key, kv.Value)).OrderByDescending(c => c.Score).Take(5).ToList();
        if (ranked.Count == 0) return new SlotMatch(SlotStatus.Unknown, null, 0, ranked);
        double top = ranked[0].Score, margin = ranked.Count > 1 ? top - ranked[1].Score : top;
        bool sure = top >= SlotSure && margin >= SlotSureMargin;
        if (sure) return new SlotMatch(SlotStatus.Recognized, ranked[0].HeroKey, top, ranked);
        return new SlotMatch(top >= SlotMaybe ? SlotStatus.Ambiguous : SlotStatus.Unknown, null, top, ranked);
    }

    public SlotMatch Match(RgbImage portrait)
    {
        if (portrait.LumaStdDev() < EmptyStdDev)
            return new SlotMatch(SlotStatus.Empty, null, 0, Array.Empty<MatchCandidate>());

        var f = PortraitFeature.From(portrait);
        List<MatchCandidate> ranked;
        lock (_sync)
        {
            ranked = _templates
                .Select(kv => new MatchCandidate(kv.Key, kv.Value.Max(t => t.Similarity(f))))
                .OrderByDescending(c => c.Score)
                .Take(5)
                .ToList();
        }

        if (ranked.Count == 0 || ranked[0].Score < Threshold)
            return new SlotMatch(SlotStatus.Unknown, null, ranked.Count > 0 ? ranked[0].Score : 0, ranked);

        if (ranked.Count > 1 && ranked[0].Score - ranked[1].Score < AmbiguityMargin)
            return new SlotMatch(SlotStatus.Ambiguous, null, ranked[0].Score, ranked);

        return new SlotMatch(SlotStatus.Recognized, ranked[0].HeroKey, ranked[0].Score, ranked);
    }
}

/// <summary>Хранилище шаблонов на диске: templates\{heroKey}\*.dpt</summary>
public sealed class TemplateStore
{
    private readonly string _root;
    public TemplateStore(string root) { _root = root; }

    public string Root => _root;

    public int LoadInto(PortraitMatcher matcher)
    {
        int n = 0;
        if (!Directory.Exists(_root)) return 0;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var key = Path.GetFileName(dir);
            foreach (var file in Directory.EnumerateFiles(dir, "*.dpt"))
            {
                try { matcher.AddTemplate(key, RgbImage.Load(file)); n++; }
                catch (Exception ex) { Log.Warn($"Шаблон {file} пропущен: {ex.Message}"); }
            }
        }
        return n;
    }

    public string Save(string heroKey, RgbImage portrait)
    {
        var dir = Path.Combine(_root, Sanitize(heroKey));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}.dpt");
        portrait.Save(path);
        return path;
    }

    private static string Sanitize(string s) =>
        new string(s.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
}
