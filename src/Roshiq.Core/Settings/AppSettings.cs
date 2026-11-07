using Roshiq.Core.Layout;
using Roshiq.Core.Models;

namespace Roshiq.Core.Settings;

/// <summary>Все пользовательские настройки. Хранятся локально в JSON.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public Language Language { get; set; } = Language.Russian;
    public Position Position { get; set; } = Position.Carry;
    public UsageContext UsageContext { get; set; } = UsageContext.Reference;
    public bool Autostart { get; set; }
    public string? LastHeroKey { get; set; }
    /// <summary>Акцентная тема интерфейса: lime, crimson, violet.</summary>
    public string Theme { get; set; } = "lime";
    /// <summary>Масштаб главного окна (0.85–1.3).</summary>
    public double UiScale { get; set; } = 1.0;
    public bool ReduceMotion { get; set; }
    /// <summary>Мягкое свечение за курсором в цвете темы (главное окно).</summary>
    public bool CursorGlow { get; set; } = true;

    public List<string> FavoriteHeroKeys { get; set; } = new();
    /// <summary>Избранные герои как личный пул: учитывать при подборе контрпиков.</summary>
    public PoolMode PoolMode { get; set; } = PoolMode.Off;
    /// <summary>Личные заметки к героям: ключ героя → текст.</summary>
    public Dictionary<string, string> HeroNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Сворачивать главное окно в область уведомлений (трей).</summary>
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>Крестик окна сворачивает в трей вместо выхода.</summary>
    public bool CloseToTray { get; set; }
    public List<OverlayProfile> OverlayProfiles { get; set; } = OverlayProfile.Defaults();
    public string? ActiveOverlayProfile { get; set; }
    public List<PersonalBuild> PersonalBuilds { get; set; } = new();
    public Dictionary<string, string> ActivePersonalBuilds { get; set; } = new();

    public OverlaySettings Overlay { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public GsiSettings Gsi { get; set; } = new();
    public VisionSettings Vision { get; set; } = new();
    public DataSettings Data { get; set; } = new();
    public TimerSettings Timers { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();
    /// <summary>Пройден мастер первого запуска (1). 0 — ещё нет.</summary>
    public int SetupVersion { get; set; }
    /// <summary>Версия ROSHIQ, для которой уже показано окно «Что нового».</summary>
    public string? LastSeenVersion { get; set; }
    /// <summary>Выбранный режим подсказок (что включено по умолчанию).</summary>
    public HintPreset Preset { get; set; } = HintPreset.Normal;
    /// <summary>Показывать расширенные настройки (калибровка, хранилище, тонкая настройка GSI).</summary>
    public bool ShowAdvancedSettings { get; set; }

    /// <summary>Приводит значения к допустимым диапазонам (после загрузки/импорта).</summary>
    public AppSettings Normalize()
    {
        if (!Enum.IsDefined(Position)) Position = Position.Carry;
        if (!Enum.IsDefined(Language)) Language = Language.Russian;
        if (!Enum.IsDefined(UsageContext)) UsageContext = UsageContext.Reference;
        if (Theme is not ("lime" or "crimson" or "violet")) Theme = "lime";
        UiScale = Math.Clamp(double.IsFinite(UiScale) ? UiScale : 1.0, 0.85, 1.3);
        FavoriteHeroKeys = (FavoriteHeroKeys ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        PersonalBuilds = (PersonalBuilds ?? new()).Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id)
            && !string.IsNullOrWhiteSpace(x.HeroKey) && Enum.IsDefined(x.Position)).DistinctBy(x => x.Id).ToList();
        foreach (var build in PersonalBuilds) build.Normalize();
        ActivePersonalBuilds = (ActivePersonalBuilds ?? new()).Where(pair => PersonalBuilds.Any(b => b.Id == pair.Value
            && PersonalBuild.ContextKey(b.HeroKey, b.Position) == pair.Key)).ToDictionary(x => x.Key, x => x.Value);
        if (!Enum.IsDefined(PoolMode)) PoolMode = PoolMode.Off;
        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in HeroNotes ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
            var text = pair.Value.Trim();
            notes[pair.Key.Trim()] = text.Length > 2000 ? text[..2000] : text;
        }
        HeroNotes = notes;
        OverlayProfiles = (OverlayProfiles ?? new()).Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id)).DistinctBy(x => x.Id).ToList();
        if (OverlayProfiles.Count == 0) OverlayProfiles = OverlayProfile.Defaults();
        foreach (var profile in OverlayProfiles) profile.Normalize();
        if (ActiveOverlayProfile != null && OverlayProfiles.All(x => x.Id != ActiveOverlayProfile)) ActiveOverlayProfile = null;
        Timers ??= new();
        Alerts ??= new();
        Automation ??= new();
        Timers.Normalize();
        Alerts.Normalize();
        Overlay ??= new();
        Notifications ??= new();
        Gsi ??= new();
        Vision ??= new();
        Data ??= new();
        if (string.IsNullOrWhiteSpace(Data.UpdateRepo)) Data.UpdateRepo = DataSettings.DefaultUpdateRepo;
        Overlay.Normalize();
        Notifications.Normalize();
        Gsi.Normalize();
        Vision.Normalize();
        return this;
    }
}

public sealed class OverlaySettings
{
    public OverlayMode Mode { get; set; } = OverlayMode.Minimal;
    /// <summary>Положение и размер панели в долях клиентской области окна игры (0..1).</summary>
    public RelRect Panel { get; set; } = new(0.78, 0.22, 0.19, 0.30);
    public double Opacity { get; set; } = 0.88;
    public string ToggleHotkey { get; set; } = "F8";
    public string EditHotkey { get; set; } = "Ctrl+Shift+F8";
    /// <summary>Переключение Minimal ↔ Expanded без мыши (оверлей не перехватывает клики).</summary>
    public string DetailsHotkey { get; set; } = "Ctrl+F8";
    /// <summary>Масштаб оверлея (0.8–1.5).</summary>
    public double Scale { get; set; } = 1.0;
    /// <summary>Отметить убийство Рошана (запускает таймер возрождения и Аегиса).</summary>
    public string RoshanHotkey { get; set; } = "Ctrl+Shift+K";
    public string TargetProcess { get; set; } = "dota2";
    public bool HideWhenGameNotFocused { get; set; } = false;

    public void Normalize()
    {
        Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : 0.88, 0.2, 1.0);
        Panel = SafeZone.Clamp(Panel ?? new RelRect(0.78, 0.22, 0.19, 0.30));
        if (string.IsNullOrWhiteSpace(ToggleHotkey)) ToggleHotkey = "F8";
        if (string.IsNullOrWhiteSpace(EditHotkey)) EditHotkey = "Ctrl+Shift+F8";
        if (string.IsNullOrWhiteSpace(DetailsHotkey)) DetailsHotkey = "Ctrl+F8";
        if (string.IsNullOrWhiteSpace(RoshanHotkey)) RoshanHotkey = "Ctrl+Shift+K";
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1.0, 0.8, 1.5);
        if (string.IsNullOrWhiteSpace(TargetProcess)) TargetProcess = "dota2";
        if (!Enum.IsDefined(Mode)) Mode = OverlayMode.Minimal;
    }
}

public sealed class NotificationSettings
{
    public bool Enabled { get; set; } = true;
    public int ObserverIntervalSec { get; set; } = 360;
    public int SentryIntervalSec { get; set; } = 420;
    public int SmokeIntervalSec { get; set; } = 600;
    public int ConsumablesIntervalSec { get; set; } = 480;
    public bool ObserverEnabled { get; set; } = true;
    public bool SentryEnabled { get; set; } = true;
    public bool SmokeEnabled { get; set; } = true;
    public bool ConsumablesEnabled { get; set; } = true;
    /// <summary>Сколько секунд показывать напоминание.</summary>
    public int DisplaySec { get; set; } = 6;
    /// <summary>Минимальная пауза между любыми двумя напоминаниями.</summary>
    public int MinGapSec { get; set; } = 45;
    /// <summary>Первое напоминание не раньше этой отметки игрового времени.</summary>
    public int FirstReminderAtSec { get; set; } = 60;

    public void Normalize()
    {
        ObserverIntervalSec = Math.Clamp(ObserverIntervalSec, 60, 3600);
        SentryIntervalSec = Math.Clamp(SentryIntervalSec, 60, 3600);
        SmokeIntervalSec = Math.Clamp(SmokeIntervalSec, 60, 3600);
        ConsumablesIntervalSec = Math.Clamp(ConsumablesIntervalSec, 60, 3600);
        DisplaySec = Math.Clamp(DisplaySec, 2, 30);
        MinGapSec = Math.Clamp(MinGapSec, 10, 600);
        FirstReminderAtSec = Math.Clamp(FirstReminderAtSec, 0, 1800);
    }
}

public sealed class GsiSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 53742;
    /// <summary>Случайный токен, которым Dota подписывает запросы (не секрет Steam).</summary>
    public string Token { get; set; } = Guid.NewGuid().ToString("N");

    public void Normalize()
    {
        if (Port is < 1024 or > 65535) Port = 53742;
        if (string.IsNullOrWhiteSpace(Token)) Token = Guid.NewGuid().ToString("N");
    }
}

public sealed class VisionSettings
{
    public bool AutoDraftEnabled { get; set; } = true;
    /// <summary>Поле старого формата настроек. Автодрафт управляется AutoDraftEnabled.</summary>
    public bool Enabled { get; set; }
    public int IntervalMs { get; set; } = 1500;
    public double MatchThreshold { get; set; } = 0.82;
    public double AmbiguityMargin { get; set; } = 0.025;
    public DraftLayoutSettings Layout { get; set; } = new();

    public void Normalize()
    {
        IntervalMs = Math.Clamp(IntervalMs, 1000, 10000);
        MatchThreshold = Math.Clamp(MatchThreshold, 0.5, 0.99);
        AmbiguityMargin = Math.Clamp(AmbiguityMargin, 0.0, 0.2);
        Layout ??= new();
    }
}

/// <summary>Геометрия портретов верхней панели в долях кадра (для 16:9). Калибруется пользователем.</summary>
public sealed class DraftLayoutSettings
{
    public double RadiantStartX { get; set; } = 0.2875;
    public double DireStartX { get; set; } = 0.5550;
    public double Top { get; set; } = 0.0;
    public double SlotWidth { get; set; } = 0.0323;
    public double SlotHeight { get; set; } = 0.033;
    public double SlotStep { get; set; } = 0.0323;
}

public sealed class DataSettings
{
    /// <summary>Разрешить загрузку статистики матчапов с OpenDota (публичный API, без ключа).</summary>
    public bool AllowOnlineStats { get; set; }
    /// <summary>Необязательный API-ключ OpenDota (повышает лимиты). Хранится только локально.</summary>
    public string? OpenDotaApiKey { get; set; }
    public int MinGamesForStats { get; set; } = 200;
    /// <summary>Steam ID (32-битный account id или 64-битный) для импорта матчей из OpenDota.</summary>
    public string? SteamAccountId { get; set; }
    /// <summary>Как привязан аккаунт: gsi (из игры), local (Steam на этом ПК), openid (вход через Steam), manual.</summary>
    public string? SteamLinkedVia { get; set; }
    public string? SteamName { get; set; }
    public string? SteamAvatarUrl { get; set; }
    /// <summary>Медаль из OpenDota (rank_tier) и место в таблице лидеров.</summary>
    public int? RankTier { get; set; }
    public int? LeaderboardRank { get; set; }
    public DateTime? ProfileCheckedUtc { get; set; }
    /// <summary>Видит ли OpenDota ваши матчи (null — неизвестно). false — в Dota 2 выключена общедоступная история матчей.</summary>
    public bool? PublicMatchData { get; set; }
    /// <summary>Учитывать вашу статистику на героях в советах драфта.</summary>
    public bool UsePersonalStats { get; set; } = true;
    /// <summary>Репозиторий GitHub с релизами ROSHIQ в виде owner/repo.</summary>
    public string? UpdateRepo { get; set; } = DefaultUpdateRepo;
    /// <summary>Официальный репозиторий ROSHIQ с релизами.</summary>
    public const string DefaultUpdateRepo = "baramobatare-rgb/ROSHIQ";
    /// <summary>Раз в день проверять новый патч, цены предметов и версию ROSHIQ (только если сеть разрешена).</summary>
    public bool AutoCheckOnline { get; set; } = true;
    /// <summary>Подтягивать обновления файлов данных (герои, таймеры, правила) с GitHub без новой версии программы.</summary>
    public bool AutoUpdateData { get; set; } = true;
    public DateTime? DataCheckedUtc { get; set; }
    /// <summary>Раз в неделю обновлять статистику матчапов OpenDota для контрпиков (в фоне, ~2 минуты).</summary>
    public bool AutoRefreshMatchups { get; set; } = true;
    /// <summary>Патч, о котором пользователь уже знает (уведомление больше не показывается).</summary>
    public string? AcknowledgedPatch { get; set; }
}

/// <summary>Таймеры игровых событий (руны, лотосы, Терзатель, Рошан).</summary>
public sealed class TimerSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Показывать всплывающее предупреждение незадолго до события.</summary>
    public bool Toasts { get; set; } = true;
    /// <summary>Ключи таймеров из timers.txt, которые выключены.</summary>
    public List<string> Disabled { get; set; } = new() { "lotus", "daynight", "water_rune" };

    public bool IsOn(string key) => Enabled && !Disabled.Contains(key, StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        Disabled = (Disabled ?? new()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Ситуативные подсказки в матче по своему инвентарю и золоту (данные GSI).</summary>
public sealed class AlertSettings
{
    public bool Enabled { get; set; } = true;
    public bool NoTeleport { get; set; } = true;
    public bool Stash { get; set; } = true;
    public bool UnspentGold { get; set; } = true;
    public int UnspentGoldThreshold { get; set; } = 2000;
    /// <summary>Напоминать, что не хватает золота на байбэк (поздняя игра).</summary>
    public bool Buyback { get; set; } = true;
    public int BuybackFromMinute { get; set; } = 30;
    /// <summary>Реже показывать подсказки, которые вы обычно игнорируете.</summary>
    public bool Adaptive { get; set; } = true;
    /// <summary>Сравнивать текущий матч с вашим обычным темпом (добивания, тайминги).</summary>
    public bool Pace { get; set; } = true;

    public void Normalize()
    {
        UnspentGoldThreshold = Math.Clamp(UnspentGoldThreshold, 500, 20000);
        BuybackFromMinute = Math.Clamp(BuybackFromMinute, 10, 90);
    }
}

/// <summary>Сохранённый набор параметров оверлея (например, «Обычный» и «Стрим»).</summary>
public sealed class OverlayProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public OverlayMode Mode { get; set; } = OverlayMode.Minimal;
    public RelRect Panel { get; set; } = new(0.78, 0.22, 0.19, 0.30);
    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 0.88;

    public static List<OverlayProfile> Defaults() => new()
    {
        new OverlayProfile { Id = "normal", Name = "Обычный / Normal" },
        new OverlayProfile { Id = "stream", Name = "Стрим / Stream", Mode = OverlayMode.Minimal, Scale = 0.9, Opacity = 0.72, Panel = new RelRect(0.80, 0.22, 0.17, 0.26) }
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Name)) Name = Id;
        if (!Enum.IsDefined(Mode)) Mode = OverlayMode.Minimal;
        Panel = SafeZone.Clamp(Panel ?? new RelRect(0.78, 0.22, 0.19, 0.30));
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1.0, 0.8, 1.5);
        Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : 0.88, 0.2, 1.0);
    }

    public void CopyFrom(OverlaySettings o)
    {
        Mode = o.Mode; Panel = o.Panel; Scale = o.Scale; Opacity = o.Opacity;
    }

    public void ApplyTo(OverlaySettings o)
    {
        o.Mode = Mode; o.Panel = Panel; o.Scale = Scale; o.Opacity = Opacity;
    }
}

/// <summary>Автоматическая работа с игрой: без нажатий в Доте — только то, что делает сам ROSHIQ.</summary>
public sealed class AutomationSettings
{
    /// <summary>Следить за запуском и закрытием Dota 2.</summary>
    public bool WatchGame { get; set; } = true;
    /// <summary>Дота запустилась — убрать главное окно в трей (оверлей остаётся).</summary>
    public bool HideWindowOnGameStart { get; set; } = true;
    /// <summary>Дота закрылась — вернуть главное окно (на разбор матча, если он был).</summary>
    public bool ShowWindowOnGameExit { get; set; } = true;
    /// <summary>Определять позицию по линии в начале матча, герою и истории.</summary>
    public bool AutoRole { get; set; } = true;
    /// <summary>После матча подтягивать результат из OpenDota (нужны Steam ID и разрешённая сеть).</summary>
    public bool AutoImport { get; set; } = true;
    /// <summary>Оверлей сам переключается: драфт → игра → «пока мёртв» → итог матча.</summary>
    public bool AutoOverlayModes { get; set; } = true;
    /// <summary>После матча просить OpenDota разобрать запись и делать разбор (нужен привязанный Steam).</summary>
    public bool AutoReview { get; set; } = true;
    /// <summary>Советы на линию против соперников (оверлей в начале игры и уведомление на старте).</summary>
    public bool LaneTips { get; set; } = true;
    /// <summary>Пики силы: план по фазам составов и уведомление о ключевом предмете.</summary>
    public bool PowerSpikes { get; set; } = true;
}

/// <summary>Режим подсказок: сколько всего ROSHIQ показывает во время игры.</summary>
public enum HintPreset { Minimal, Normal, Full }

public static class HintPresets
{
    /// <summary>
    /// Применить режим: Минимум — только самое важное (нет ТП, выкуп, таймеры без всплывающих окон);
    /// Обычный — значения по умолчанию; Всё — все подсказки и уведомления.
    /// </summary>
    public static void Apply(AppSettings s, HintPreset preset)
    {
        s.Preset = preset;
        bool full = preset == HintPreset.Full, minimal = preset == HintPreset.Minimal;
        s.Timers.Enabled = true;
        s.Timers.Toasts = !minimal;
        s.Alerts.Enabled = true;
        s.Alerts.NoTeleport = true;
        s.Alerts.Buyback = true;
        s.Alerts.Stash = !minimal;
        s.Alerts.UnspentGold = !minimal;
        s.Alerts.Pace = !minimal;
        s.Alerts.Adaptive = !full;
        s.Automation.LaneTips = !minimal;
        s.Automation.PowerSpikes = !minimal;
        s.Automation.AutoOverlayModes = true;
        if (full) s.Timers.Disabled.Clear();
        else if (minimal) s.Timers.Disabled = new() { "lotus", "daynight", "water_rune", "wisdom_rune", "stack" };
        else s.Timers.Disabled = new() { "lotus", "daynight", "water_rune" };
    }
}
