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
    public bool MaybeRun(GameWindowState game, bool heroSelectionByGsi, bool manualRequest, long sessionId = 0)
    {
        var v = _settings.Current.Vision;
        if (!IsAllowed || !game.Found || game.Minimized || !game.Foreground || game.ClientArea.IsEmpty) return false;
        if (!manualRequest && !(v.AutoDraftEnabled && heroSelectionByGsi)) return false;
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
                if (img == null) { LastError = "capture_failed"; return; }
                // Пересчитываем относительную разметку под полосу (Top/SlotHeight заданы от полного кадра)
                var scaled = new DraftLayoutSettings
                {
                    RadiantStartX = layout.RadiantStartX,
                    DireStartX = layout.DireStartX,
                    SlotWidth = layout.SlotWidth,
                    SlotStep = layout.SlotStep,
                    Top = layout.Top * area.Height / strip.Height,
                    SlotHeight = layout.SlotHeight * area.Height / strip.Height
                };
                var result = Recognizer.Recognize(img, scaled) with { SessionId = sessionId, IsManual = manualRequest };
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

    /// <summary>Ручное исправление: запомнить портрет слота как шаблон героя.</summary>
    public void Teach(string heroKey, RgbImage crop)
    {
        Store.Save(heroKey, crop);
        Matcher.AddTemplate(heroKey, crop);
    }
}
