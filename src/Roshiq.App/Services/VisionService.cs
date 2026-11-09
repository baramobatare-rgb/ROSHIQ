using System.IO.Compression;
using Roshiq.Core.Compliance;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Layout;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Overlay;
using Roshiq.Vision;

namespace Roshiq.App.Services;

/// <summary>
/// Local recognition of visible portraits, enabled by AutoDraftEnabled during GSI hero selection.
/// Captures only the configured strip while Dota 2 is foreground; the worker does not overlap itself.
/// </summary>
public sealed class VisionService
{
    private readonly SettingsService _settings;
    private readonly RecognitionThrottle _throttle = new();

    public VisionService(SettingsService settings)
    {
        _settings = settings;
        Matcher = new PortraitMatcher();
        Store = new TemplateStore(AppPaths.Templates);
        Recognizer = new DraftRecognizer(Matcher);
        ReloadTemplates();
    }

    public PortraitMatcher Matcher { get; }
    public TemplateStore Store { get; }
    public DraftRecognizer Recognizer { get; }
    public DraftRecognitionResult? LastResult { get; private set; }
    public DateTime? LastRunUtc { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler<DraftRecognitionResult>? Recognized;

    public int ReloadTemplates()
    {
        Matcher.Clear();
        int n = 0;
        using (var resource = typeof(VisionService).Assembly.GetManifestResourceStream("Roshiq.App.Assets.Portraits.zip"))
        {
            if (resource != null)
            {
                using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries.Where(x => x.Name.EndsWith(".dpt", StringComparison.OrdinalIgnoreCase)))
                {
                    using var data = entry.Open();
                    Matcher.AddTemplate(entry.FullName.Split('/')[0], RgbImage.Load(data));
                    n++;
                }
            }
        }
        n += Store.LoadInto(Matcher);
        Log.Info($"Vision: загружено шаблонов {n}");
        return n;
    }

    public bool IsAllowed => ComplianceGate.IsAllowed(Feature.AutoDraftRecognition, _settings.Current.UsageContext);

    /// <summary>Вызывается периодически. Возвращает true, если запущено распознавание.</summary>
    public bool MaybeRun(GameWindowState game, bool heroSelectionByGsi, bool manualRequest, long sessionId = 0, IntPtr gameWindow = default)
    {
        var v = _settings.Current.Vision;
        bool wanted = manualRequest || (v.AutoDraftEnabled && heroSelectionByGsi);
        string? blocked = !IsAllowed ? "режим не разрешает" : !game.Found ? "окно Dota не найдено" : game.Minimized ? "Dota свёрнута"
            : !game.Foreground ? "Dota не активное окно" : game.ClientArea.IsEmpty ? "пустая область окна" : null;
        if (blocked != null)
        {
            if (wanted) LogOnce(sessionId, "gate:" + blocked, "Vision: пропуск — " + blocked);
            return false;
        }
        if (!wanted) return false;
        var interval = manualRequest ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMilliseconds(v.IntervalMs);
        if (!_throttle.TryBegin(DateTime.UtcNow, interval)) return false;

        Matcher.Threshold = v.MatchThreshold;
        Matcher.AmbiguityMargin = v.AmbiguityMargin;
        var layout = v.Layout;
        var area = game.ClientArea;
        // захватываем только верхнюю полосу с портретами, а не весь экран
        var strip = new PixelRect(area.X, area.Y, area.Width, Math.Max(8, (int)(area.Height * Math.Min(0.25, layout.Top + layout.SlotHeight * 2 + 0.01))));

        Task.Run(() =>
        {
            try
            {
                var img = ScreenCapture.Capture(strip);
                if (img == null)
                {
                    LastError = "capture_failed";
                    LogOnce(sessionId, "capture_null", $"Vision: захват не удался (область {strip.Width}×{strip.Height})");
                    return;
                }
                // Чёрный кадр с экрана — пробуем попросить окно игры отрисоваться в наш буфер (PrintWindow)
                if (IsBlack(img) && ScreenCapture.CaptureWindowTop(gameWindow, strip.Width, strip.Height) is { } fromWindow && !IsBlack(fromWindow))
                {
                    LogOnce(sessionId, "capture_window", "Vision: экранный кадр чёрный — беру картинку из окна игры (PrintWindow)");
                    img = fromWindow;
                }
                // Всё ещё чёрный — обычно эксклюзивный полноэкранный режим Dota: так не захватить
                if (IsBlack(img))
                {
                    LastError = "capture_black";
                    LogOnce(sessionId, "capture_black", $"Vision: кадр чёрный ({img.Width}×{img.Height}) — нужен режим «окно без рамки»");
                    SaveDebugFrame(img, sessionId, "black");
                    return;
                }
                // В кадре только верхняя полоса — разметка считается от высоты всего окна игры
                var result = Recognizer.Recognize(img, layout, area.Height) with { SessionId = sessionId, IsManual = manualRequest };
                LogResult(result, img, sessionId);
                LastResult = result;
                LastRunUtc = DateTime.UtcNow;
                LastError = null;
                Recognized?.Invoke(this, result);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("Vision: ошибка распознавания", ex);
            }
            finally
            {
                _throttle.End();
            }
        });
        return true;
    }

    // ------------------------------------------------------------------ диагностика: что видит распознавание

    private readonly object _diagSync = new();
    private readonly HashSet<string> _logged = new();
    private long _diagSession = long.MinValue;
    private string? _lastSummary;
    private int _framesSaved;

    /// <summary>Пишет сообщение в журнал один раз за сеанс драфта (чтобы не засорять журнал каждые 1,5 с).</summary>
    private void LogOnce(long sessionId, string key, string message)
    {
        lock (_diagSync)
        {
            ResetDiagIfNewSession(sessionId);
            if (_logged.Add(key)) Log.Info(message);
        }
    }

    private void ResetDiagIfNewSession(long sessionId)
    {
        if (sessionId == _diagSession) return;
        _diagSession = sessionId;
        _logged.Clear();
        _lastSummary = null;
        _framesSaved = 0;
    }

    /// <summary>Итог кадра в журнал — только когда он изменился. Первые кадры сеанса сохраняются картинкой для настройки разметки.</summary>
    private void LogResult(DraftRecognitionResult r, RgbImage img, long sessionId)
    {
        int ok = r.Slots.Count(s => s.Match.Status == SlotStatus.Recognized);
        int unsure = r.UncertainCount;
        int empty = r.Slots.Count(s => s.Match.Status == SlotStatus.Empty);
        double best = r.Slots.Where(s => s.Match.Status != SlotStatus.Empty).Select(s => s.Match.Score).DefaultIfEmpty(0).Max();
        var heroes = string.Join(", ", r.Slots.Where(s => s.Match.Status == SlotStatus.Recognized).Select(s => s.Match.HeroKey));
        var summary = $"распознано {ok}, неуверенно {unsure}, пусто {empty}";
        bool changed;
        lock (_diagSync)
        {
            ResetDiagIfNewSession(sessionId);
            changed = summary != _lastSummary;
            _lastSummary = summary;
        }
        if (!changed) return;
        Log.Info($"Vision: кадр {img.Width}×{img.Height}, интерфейс драфта {(r.DraftUiVisible ? "виден" : "не найден")}, {summary}, " +
                 $"лучшее совпадение {best:0.00}" + (heroes.Length > 0 ? $" ({heroes})" : ""));
        // Кадр сохраняем при каждом изменении итога: так видно и пустые слоты, и момент, когда появились выбранные герои
        SaveDebugFrame(img, sessionId, !r.DraftUiVisible ? "noui" : unsure > 0 ? "unsure" : "ok");
    }

    private static bool IsBlack(RgbImage img)
    {
        long sum = 0;
        int step = Math.Max(3, img.Data.Length / 3 / 4000) * 3;     // ~4000 точек хватает
        int n = 0;
        for (int i = 0; i + 2 < img.Data.Length; i += step) { sum += img.Data[i] + img.Data[i + 1] + img.Data[i + 2]; n++; }
        return n > 0 && sum / (double)(n * 3) < 4;
    }

    /// <summary>
    /// Сохраняет полосу с портретами (только верх экрана драфта) в logs\vision — по ней видно, совпадает ли разметка слотов.
    /// Не больше восьми кадров за сеанс и двадцати четырёх файлов всего (около 1 МБ на кадр 2560 px).
    /// </summary>
    private void SaveDebugFrame(RgbImage img, long sessionId, string tag)
    {
        lock (_diagSync)
        {
            ResetDiagIfNewSession(sessionId);
            if (_framesSaved >= 8) return;
            _framesSaved++;
        }
        try
        {
            var dir = Path.Combine(AppPaths.Logs, "vision");
            Directory.CreateDirectory(dir);
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(img.Width, img.Height, 96, 96,
                System.Windows.Media.PixelFormats.Rgb24, null, img.Data, img.Width * 3);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            var file = Path.Combine(dir, $"draft-{DateTime.Now:yyyyMMdd-HHmmss}-{tag}.png");
            using (var fs = File.Create(file)) enc.Save(fs);
            foreach (var old in new DirectoryInfo(dir).GetFiles("draft-*.png").OrderByDescending(f => f.CreationTimeUtc).Skip(24))
                try { old.Delete(); } catch { }
        }
        catch (Exception ex) { Log.Warn("Vision: кадр не сохранён: " + ex.Message); }
    }

    /// <summary>Ручное исправление: запомнить портрет слота как шаблон героя.</summary>
    public void Teach(string heroKey, RgbImage crop)
    {
        Store.Save(heroKey, crop);
        Matcher.AddTemplate(heroKey, crop);
    }
}
