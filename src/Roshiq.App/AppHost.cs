using System.Windows.Threading;
using Roshiq.App.Services;
using Roshiq.Core.Compliance;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Core.Updates;
using Roshiq.Data;
using Roshiq.Draft;
using Roshiq.GSI;
using Roshiq.Overlay;
using Roshiq.Recommendations;
using Roshiq.Vision;

namespace Roshiq.App;

/// <summary>
/// Композиция всех модулей и логика "что показать в оверлее".
/// Сбой любого модуля логируется и не останавливает остальные.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly DispatcherTimer _tick;
    private readonly Dispatcher _dispatcher;
    private string _lastToggleHotkey = "";
    private string _lastEditHotkey = "";
    private string _lastDetailsHotkey = "";
    private string _lastRoshanHotkey = "";
    private readonly TimerAnnouncer _timerAnnouncer = new();
    private readonly MatchRecorder _recorder = new();
    private readonly HashSet<int> _notedEnemies = new();
    /// <summary>Единый диспетчер всплывающих советов: очередь, срочность, срок жизни, снятие неактуального.</summary>
    public TipDispatcher Tips { get; } = new();
    private IReadOnlyList<string> _alertActive = Array.Empty<string>();
    private bool _onlineCheckRunning;
    private PaceAdvisor? _pace;
    private string? _paceMatchId;
    private readonly HashSet<string> _paceItemsSeen = new(StringComparer.OrdinalIgnoreCase);
    private int _lastGsiPort;
    private string _lastGsiToken = "";
    private bool _lastGsiEnabled;
    private bool _manualScanRequested;
    private DateTime? _manualTimerStartUtc;
    private string? _lastMatchId;
    private readonly DraftPickTracker _autoPicks = new();
    private readonly LaneRoleDetector _roleDetector = new();
    private bool _queueRoleApplied;
    private bool _queueHasMultiple;
    private bool _rolePromptShown;
    private DateTime _roleSessionStartedUtc;
    private DateTime _queueRoleLastCheckUtc;
    private readonly GameLaunchWatcher _gameWatch = new();
    private readonly HashSet<string> _importPending = new(StringComparer.Ordinal);
    private MatchRecord? _lastSaved;
    // 0.17.5: матчи, которые не удалось записать на диск (повторяем), и контрольная запись незаконченного матча
    private readonly List<MatchRecord> _unsaved = new();
    private DateTime _unsavedRetryUtc;
    private bool _saveFailWarned;
    private (MatchRecord Record, DateTime SavedUtc)? _pendingCheckpoint;
    private DateTime _lastCheckpointUtc;
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckpointMaxAge = TimeSpan.FromHours(3);
    private static readonly TimeSpan UnsavedRetryEvery = TimeSpan.FromSeconds(30);
    private string? _autoSessionKey;
    private DateTime _autoStartedUtc;
    public long AutoDraftGeneration { get; private set; }
    public DraftAnalysis? AutoDraftAnalysis { get; private set; }
    public bool AutoDraftLive => Settings.Current.Vision.AutoDraftEnabled && LastGsi != null &&
        DateTime.UtcNow - LastGsi.ReceivedUtc < TimeSpan.FromSeconds(40) && LastGsi.IsHeroSelection;
    public IReadOnlyList<Hero> AutoEnemies => _autoPicks.Heroes(MySide == Side.Radiant ? Side.Dire : Side.Radiant)
        .Select(Database.FindHero).Where(x => x != null).Select(x => x!).ToList();


    public AppHost()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = new SettingsService();
        Roles = new MatchRoleState(Settings.Current.Position);
        Loc.Instance.Language = Settings.Current.Language;

        Database = GameDatabase.Load(AppPaths.DataOverrides, Settings.Current.Data.AutoUpdateData ? RemoteData.UsableRemoteDir(AppPaths.DataRemote) : null);
        Stats = new MatchupStatsCache(Path.Combine(AppPaths.Cache, "matchups.json"));
        Analyzer = new DraftAnalyzer(Database, Stats.HeroesCached > 0 ? Stats : NoStatsProvider.Instance, Settings.Current.Data.MinGamesForStats);
        Builds = new ItemBuildEngine(Database);
        Advisor = new RoleAdvisor(Database);
        Reminders = new SupportReminderScheduler(Settings.Current.Notifications);
        OverlayVm = new OverlayViewModel();
        Overlay = new OverlayController(Settings, OverlayVm);
        Vision = new VisionService(Settings);
        Sound = new SoundService(Settings);
        Monitor = new ProcessMonitor();
        Draft = DraftState.Empty(CurrentPosition);
        CurrentHero = Database.FindHero(Settings.Current.LastHeroKey);
        Costs = new ItemCostCache(Path.Combine(AppPaths.Cache, "item_costs.json"));
        PlayerHeroes = new PlayerHeroStatsCache(Path.Combine(AppPaths.Cache, "player_heroes.json"));
        Reviews = new MatchReviewStore(Path.Combine(AppPaths.Cache, "reviews"));
        ItemNorms = new ItemTimingNormCache(Path.Combine(AppPaths.Cache, "item_timings.json"));
        MetaBuilds = new MetaBuildCache(Path.Combine(AppPaths.Cache, "meta_builds.json"));
        Patch = new PatchInfoCache(Path.Combine(AppPaths.Cache, "patch.json"));
        PatchNotes = PatchDigest.Load(PatchNotesPath);
        History = new MatchHistoryStore(AppPaths.Matches);
        Goals = new GoalStore(Path.Combine(AppPaths.DataRoot, "goals.json"));
        Goals.Changed += (_, _) => _dispatcher.InvokeAsync(RaiseChanged);
        Trainer = new DraftTrainer(Database, new DraftAnalyzer(Database, Stats.HeroesCached > 0 ? Stats : NoStatsProvider.Instance, Settings.Current.Data.MinGamesForStats));
        ApplyPool();

        Reminders.Shown += (_, r) =>
        {
            if (Overlay.ShowToast((Loc.T("role_short_" + (int)CurrentPosition) + " / " + Loc.T("sec_assistant")).ToUpperInvariant(),
                    Loc.T(r.TextKey), warning: false))
                Sound.Play(SoundKind.Reminder);
        };
        Reminders.Hidden += (_, _) => Overlay.HideToast();
        Tips.Outcome += OnTipOutcome;
        Vision.Recognized += (_, r) => _dispatcher.InvokeAsync(() => OnVisionResult(r));
        Settings.Changed += (_, _) => ApplySettings();
        Overlay.DraftRoleRequested += (_, _) => SelectPosition((Position)((int)CurrentPosition % 5 + 1));
        Overlay.RoshanKillMarked += (_, _) => _dispatcher.InvokeAsync(ToggleRoshanMark);

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
    }

    public SettingsService Settings { get; }
    public MatchRoleState Roles { get; }
    public Position CurrentPosition => Roles.CurrentPosition;
    public bool QueueHasMultiple => _queueHasMultiple;
    public GameDatabase Database { get; private set; }
    public MatchupStatsCache Stats { get; }
    public DraftAnalyzer Analyzer { get; private set; }
    public ItemBuildEngine Builds { get; private set; }
    public RoleAdvisor Advisor { get; private set; }
    public SupportReminderScheduler Reminders { get; }
    public OverlayViewModel OverlayVm { get; }
    public OverlayController Overlay { get; }
    public VisionService Vision { get; }
    /// <summary>Звуки уведомлений (по желанию).</summary>
    public SoundService Sound { get; }
    public ProcessMonitor Monitor { get; }
    public GsiServer? Gsi { get; private set; }
    public ItemCostCache Costs { get; }
    /// <summary>Ваша статистика по героям из OpenDota (привязанный Steam).</summary>
    public PlayerHeroStatsCache PlayerHeroes { get; }
    /// <summary>Разборы матчей через OpenDota.</summary>
    public MatchReviewStore Reviews { get; }
    public ItemTimingNormCache ItemNorms { get; }
    public MetaBuildCache MetaBuilds { get; }
    /// <summary>Изменилась привязка Steam или профиль.</summary>
    public event EventHandler? SteamChanged;
    public PatchInfoCache Patch { get; }
    /// <summary>Изменения героев в последнем патче (OpenDota), если загружены.</summary>
    public PatchDigest? PatchNotes { get; private set; }
    private static string PatchNotesPath => Path.Combine(AppPaths.Cache, "patchnotes.json");
    public MatchHistoryStore History { get; }
    public GoalStore Goals { get; }
    public DraftTrainer Trainer { get; private set; }
    public MatchAlerts Alerts { get; } = new();
    public RoshanTracker Roshan { get; } = new();
    /// <summary>Найденное обновление ROSHIQ на GitHub (если проверка включена).</summary>
    public ReleaseInfo? AvailableUpdate { get; private set; }
    /// <summary>0.17.4: единая проверка обновлений — один запрос на всех, кэш, пауза после лимита GitHub, резервный источник.</summary>
    public UpdateCoordinator Updates { get; } = new(ReleaseChecker.CreateDefault(AppUpdater.CurrentVersion), () => AppUpdater.CurrentVersion);

    public Hero? CurrentHero { get; private set; }
    public GameStateSnapshot? LastGsi { get; private set; }
    public DraftState Draft { get; private set; }
    public DraftAnalysis? LastDraftAnalysis { get; private set; }
    public BuildRecommendation? CurrentBuild { get; private set; }
    /// <summary>Сторона игрока для распознавания (из GSI player.team_name или выбранная вручную).</summary>
    public Side MySide { get; set; } = Side.Radiant;

    public bool ManualTimerRunning => _manualTimerStartUtc != null;

    public event EventHandler? StateChanged;
    /// <summary>Dota 2 запустилась (окно игры появилось и держится несколько секунд).</summary>
    public event EventHandler? GameLaunched;
    /// <summary>Dota 2 закрылась.</summary>
    public event EventHandler? GameClosed;
    /// <summary>С момента запуска игры был сохранён хотя бы один матч.</summary>
    public bool MatchSavedSinceGameStart { get; private set; }
    /// <summary>Последний сохранённый матч (для итога в оверлее).</summary>
    public MatchRecord? LastSavedMatch => _lastSaved;

    /// <summary>Безопасный режим: без оверлея, распознавания и фоновых загрузок (после повторных сбоев при запуске).</summary>
    public bool SafeMode { get; set; }

    /// <summary>Сторож связи с игрой (Dota запущена, а данных GSI нет).</summary>
    public GsiWatchdog GsiWatch { get; } = new();
    public ConnectionState ConnectionState => ConnectionHealth.Evaluate(Settings.Current.Gsi.Enabled, Gsi?.IsRunning == true,
        Overlay.Game.Found, Gsi?.LastPacketUtc, GsiWatch.GameSinceUtc, GsiWatch.Current, DateTime.UtcNow);
    /// <summary>Почему подсказки сейчас молчат: нет связи, меню, драфт, пауза, скрытая панель.</summary>
    public TipSilence TipSilence
    {
        get
        {
            var g = LastGsi;
            bool fresh = g != null && DateTime.UtcNow - g.ReceivedUtc < ConnectionHealth.Fresh;
            return ConnectionHealth.Silence(ConnectionState, fresh ? g!.GameState : null, fresh && g!.Paused == true, Overlay.HiddenByUser);
        }
    }
    /// <summary>Советы, отложенные до разбора, пока запись матча ещё не началась (совет по линии приходит на 0:00).</summary>
    private readonly List<DeferredMatchTip> _deferredEarly = new();
    /// <summary>Журнал подсказок по матчам (logs\tips, только на этом компьютере).</summary>
    public TipJournal TipLog { get; } = new(Path.Combine(AppPaths.Logs, "tips"));

    /// <summary>Dota запущена, а данные GSI не приходят (или приходят с чужим ключом).</summary>
    public event EventHandler<GsiProblem>? GsiProblemDetected;

    public void Start()
    {
        _ = MarkStableLaterAsync();
        try { LoadUnsavedQueue(); } catch (Exception ex) { Log.Error("История: очередь несохранённых матчей", ex); }
        try { LoadPendingCheckpoint(); } catch (Exception ex) { Log.Error("История: контрольная запись", ex); }
        if (SafeMode)
        {
            Log.Warn("Безопасный режим: оверлей, распознавание и фоновые загрузки выключены");
            RestartGsiIfNeeded(force: true);
            RecomputeBuild();
            _tick.Start();
            return;
        }
        Overlay.Start();
        _lastToggleHotkey = Settings.Current.Overlay.ToggleHotkey;
        _lastEditHotkey = Settings.Current.Overlay.EditHotkey;
        _lastDetailsHotkey = Settings.Current.Overlay.DetailsHotkey;
        _lastRoshanHotkey = Settings.Current.Overlay.RoshanHotkey;
        RestartGsiIfNeeded(force: true);
        RecomputeBuild();
        _tick.Start();
        Overlay.StateChanged += (_, _) => RaiseChanged();
        _ = RunOnlineChecksAsync(force: false);
        _ = StartupImportAsync();
        _ = AutoRefreshMatchupsAsync();
    }

    // ------------------------------------------------------------------ события

    private void OnTick()
    {
        try
        {
            Monitor.Sample();
            WatchGame();
            var gsiFresh = LastGsi != null && DateTime.UtcNow - LastGsi.ReceivedUtc < TimeSpan.FromSeconds(10);

            // Напоминания: по игровым часам GSI или по ручному таймеру матча
            TimeSpan? clock = null;
            bool inProgress = false;
            if (gsiFresh && LastGsi!.ClockTime != null && (LastGsi.IsInProgress))
            {
                clock = TimeSpan.FromSeconds(LastGsi.ClockTime.Value);
                inProgress = LastGsi.Paused != true;
            }
            else if (_manualTimerStartUtc != null)
            {
                clock = DateTime.UtcNow - _manualTimerStartUtc.Value;
                inProgress = true;
            }
            if (clock != null) Reminders.Tick(clock.Value, CurrentPosition, inProgress);
            if (clock != null && inProgress) TickMatchEvents((int)Math.Floor(clock.Value.TotalSeconds));
            if (gsiFresh && LastGsi!.IsInProgress && LastGsi.Paused != true && LastGsi.HasHero && LastGsi.ClockTime is int gsiClock)
            {
                TickAlerts(gsiClock);
                TickPace(gsiClock);
            }

            // Распознавание драфта (только тренировочный контекст)
            UpdateAutoDraftSession();
            TickDraftProblem();
            if (!SafeMode)
            {
                string? rolePlayerName = Settings.Current.Automation.AutoRole && LastGsi?.IsHeroSelection == true &&
                    Roles.Active && Roles.Source < MatchRoleSource.Screen && GsiFresh
                    ? LastGsi.PlayerName ?? Settings.Current.Data.SteamName : null;
                bool started = Vision.MaybeRun(Overlay.Game, AutoDraftLive, _manualScanRequested, AutoDraftGeneration,
                    Overlay.GameWindowHandle, rolePlayerName);
                if (started) _manualScanRequested = false;
            }
            TickGsiWatchdog();
            RetryUnsavedMatches();
            PumpTips();

            RefreshOverlay();
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка такта приложения", ex);
        }
    }

    private string? _lastGameState;

    private void OnGsi(GameStateSnapshot snap)
    {
        LastGsi = snap;
        if ((snap.IsHeroSelection || snap.IsInProgress) && Roles.Enter(snap.MatchId))
        {
            Roshiq.App.Views.RolePromptWindow.CloseIfOpen();
            _queueRoleApplied = false;
            _queueHasMultiple = false;
            _rolePromptShown = false;
            _roleSessionStartedUtc = DateTime.UtcNow;
            _queueRoleLastCheckUtc = DateTime.MinValue;
            _roleDetector.Reset();
            OnEffectiveRoleChanged();
        }
        if (snap.GameState != _lastGameState)
        {
            _lastGameState = snap.GameState;
            Log.Info("GSI: стадия игры " + (snap.GameState ?? "нет данных"));
        }
        try
        {
            ResolvePendingCheckpoint(snap.MatchId);
            var completed = _recorder.Observe(ToTick(snap));
            if (completed != null) SaveMatch(completed);
            if (_recorder.Current is MatchRecord cur && cur.Id != _paceMatchId) OnMatchStarted(cur);
            if (_deferredEarly.Count > 0 && _recorder.Current is MatchRecord rec) FlushDeferred(rec);
            if (_recorder.Current is MatchRecord live && DateTime.UtcNow - _lastCheckpointUtc >= CheckpointEvery)
            {
                _lastCheckpointUtc = DateTime.UtcNow;
                History.SaveCheckpoint(live);
            }
        }
        catch (Exception ex) { Log.Error("История матча: ошибка записи", ex); }
        if (snap.IsPostGame && Roles.Leave()) OnEffectiveRoleChanged();
        if (snap.MatchId != null && snap.MatchId != _lastMatchId)
        {
            _lastMatchId = snap.MatchId;
            Reminders.Reset();
            Alerts.Reset();
            Tips.Clear(t => t.InGameOnly);          // советы прошлого матча не переносим
            _deferredEarly.Clear();
            _alertActive = Array.Empty<string>();
            _timerAnnouncer.Reset();
            Roshan.Clear();
            _roleDetector.Reset();
            if (_queueRoleApplied && snap.IsHeroSelection) _roleDetector.AcceptAssignedRole();
            _spikesShown.Clear();
            _spikeItems.Clear();
            _spikeSeeded = false;
        }
        if (string.Equals(snap.TeamName, "dire", StringComparison.OrdinalIgnoreCase)) MySide = Side.Dire;
        else if (string.Equals(snap.TeamName, "radiant", StringComparison.OrdinalIgnoreCase)) MySide = Side.Radiant;

        UpdateAutoDraftSession();
        Hero? hero = snap.HeroId is > 0 ? Database.FindHero(snap.HeroId.Value) : null;
        hero ??= Database.FindHero(snap.HeroName);
        if (hero != null && hero.Id != CurrentHero?.Id) SelectHero(hero);
        TryLinkFromGsi(snap);
        TickRankedRoleSelection(snap);
        TickRoleDetection(snap);
        TickGameInsights(snap);
        RaiseChanged();
    }

    public string AutoDraftStatus
    {
        get
        {
            if (!Settings.Current.Vision.AutoDraftEnabled) return Loc.T("auto_off");
            if (LastGsi == null || DateTime.UtcNow - LastGsi.ReceivedUtc >= TimeSpan.FromSeconds(40)) return Loc.T("auto_need_gsi");
            if (!AutoDraftLive) return Loc.T("auto_wait_draft");
            if (!SideKnown) return Loc.T("auto_need_side");
            if (Vision.Matcher.HeroCount == 0) return Loc.T("auto_no_templates");
            if (Vision.LastError != null) return Loc.T("auto_capture_failed");
            if (!Overlay.Game.Foreground && Overlay.Game.Found) return Loc.T("auto_focus");
            if (AutoEnemies.Count > 0) return Loc.Instance.Format("auto_detected", string.Join(", ", AutoEnemies.Select(h => h.Name)));
            if (DateTime.UtcNow - _autoStartedUtc > TimeSpan.FromSeconds(15) && Vision.LastRunUtc >= _autoStartedUtc && Vision.LastResult?.UncertainCount > 0)
                return Loc.T("auto_calibrate");
            return Loc.T("auto_wait_picks");
        }
    }

    private void UpdateAutoDraftSession()
    {
        string? key = AutoDraftLive ? (LastGsi!.MatchId ?? "unidentified") + ":" + LastGsi.TeamName : null;
        if (key == _autoSessionKey) return;
        if (key == null && _autoSessionKey != null)
            Log.Info($"Авто-драфт: закончился. Союзники: {Names(_autoPicks.Heroes(MySide))}; враги: {Names(AutoEnemies.Select(h => h.Key))}");
        if (key != null)
            Log.Info($"Авто-драфт: начался (сторона {LastGsi!.TeamName ?? "?"}, шаблонов {Vision.Matcher.HeroCount}, окно Dota " +
                     $"{(Overlay.Game.Found ? (Overlay.Game.Foreground ? "активно" : "не активно") : "не найдено")})");
        _autoSessionKey = key;
        AutoDraftGeneration++;
        _autoStartedUtc = DateTime.UtcNow;
        _inferredSide = null;
        _countersToasted = "";
        _draftProblemToasted = false;
        _autoPicks.Reset();
        _notedEnemies.Clear();
        AutoDraftAnalysis = null;
    }

    private static string Names(IEnumerable<string> keys)
    {
        var list = keys.ToList();
        return list.Count == 0 ? "—" : string.Join(", ", list);
    }

    // ------------------------------------------------------------------ авто-драфт: сторона, всплывающие контрпики, подсказка о сбое

    private Side? _inferredSide;
    private string _countersToasted = "";
    private bool _draftProblemToasted;

    /// <summary>Сторона известна: из GSI, а если GSI её не прислал — по тому, на чьей половине распознан ваш герой.</summary>
    private bool SideKnown => LastGsi?.TeamName is "radiant" or "dire" || _inferredSide != null;

    private void InferSide(DraftRecognitionResult result)
    {
        if (LastGsi?.TeamName is "radiant" or "dire" || _inferredSide != null) return;
        if (LastGsi?.HeroId is not int heroId || heroId <= 0 || Database.FindHero(heroId) is not { } mine) return;
        foreach (var side in new[] { Side.Radiant, Side.Dire })
            if (result.For(side).Any(s => s.Match.Status == SlotStatus.Recognized && s.Match.HeroKey == mine.Key))
            {
                _inferredSide = side;
                MySide = side;
                Log.Info($"Авто-драфт: GSI не прислал сторону — по портрету вашего героя это {side}");
                return;
            }
    }

    /// <summary>Новые пики врагов — всплывающее уведомление с лучшими контрпиками (если включено в настройках).</summary>
    private void AnnounceCounters()
    {
        if (!Settings.Current.Automation.DraftCounterToasts || AutoDraftAnalysis is not { Top.Count: > 0 } a) return;
        var enemies = AutoEnemies;
        var key = string.Join(",", enemies.Select(h => h.Id).OrderBy(x => x));
        if (key == _countersToasted) return;
        _countersToasted = key;
        var L = Loc.Instance;
        ShowEventToast(L.Get("auto_toast_header").ToUpperInvariant(),
            L.Format("auto_toast", string.Join(", ", enemies.Select(h => h.DisplayName(L.Language))),
                string.Join(", ", a.Top.Take(3).Select(r => r.Hero.DisplayName(L.Language)))),
            kind: "draft_counters", key: "draft_counters", ttlSec: 30);
    }

    /// <summary>Через 30 секунд драфта врагов так и не видно — один раз объяснить, что мешает (тем же текстом, что в «Драфте»).</summary>
    private void TickDraftProblem()
    {
        if (!AutoDraftLive || _draftProblemToasted || AutoEnemies.Count > 0 || DateTime.UtcNow - _autoStartedUtc < TimeSpan.FromSeconds(30)) return;
        _draftProblemToasted = true;
        var status = AutoDraftStatus;
        Log.Info("Авто-драфт: за 30 с врагов не распознано — " + status);
        ShowEventToast(Loc.T("auto_toast_header").ToUpperInvariant(), status, warning: true, kind: "draft_problem", ttlSec: 30);
    }

    private void RecomputeAutoDraft()
    {
        if (!AutoDraftLive || !SideKnown || AutoEnemies.Count == 0)
        { AutoDraftAnalysis = null; return; }
        var allies = _autoPicks.Heroes(MySide).Select(Database.FindHero).Where(x => x != null).Select(x => x!).ToList();
        AutoDraftAnalysis = Analyzer.Analyze(new DraftState(allies, AutoEnemies, CurrentPosition), countersOnly: true);
    }

    private void OnVisionResult(DraftRecognitionResult result)
    {
        if (result.SessionId != AutoDraftGeneration) return;
        if (result.AssignedPosition is Position screenRole &&
            Roles.ApplyAutomatic(screenRole, MatchRoleSource.Screen, "подпись роли в драфте"))
        {
            Log.Info($"Роль с экрана драфта: {screenRole}");
            _roleDetector.AcceptAssignedRole();
            OnEffectiveRoleChanged();
        }
        if (result.IsManual && !AutoDraftLive)
        {
            List<Hero> Read(Side side) => result.For(side).Where(x => x.Match.Status == SlotStatus.Recognized)
                .Select(x => Database.FindHero(x.Match.HeroKey)).Where(x => x != null).Select(x => x!).DistinctBy(x => x.Id).ToList();
            SetDraft(Read(MySide), Read(MySide == Side.Radiant ? Side.Dire : Side.Radiant));
            return;
        }
        InferSide(result);
        if (!AutoDraftLive || !SideKnown) return;
        _autoPicks.Apply(result);
        RecomputeAutoDraft();
        AnnounceCounters();
        AnnounceEnemyNotes(AutoEnemies);
        var allies = _autoPicks.Heroes(MySide).Select(Database.FindHero).Where(x => x != null).Select(x => x!).ToList();
        // Keep the main draft view in sync with confirmed screen picks.
        SetDraft(allies, AutoEnemies);
        RefreshOverlay();
    }

    // ------------------------------------------------------------------ действия

    public void SelectHero(Hero? hero)
    {
        CurrentHero = hero;
        Settings.Current.LastHeroKey = hero?.Key;
        Settings.Save();
        RecomputeBuild();
        RaiseChanged();
    }

    public void SetDraft(IReadOnlyList<Hero> allies, IReadOnlyList<Hero> enemies)
    {
        _draftMatchId = LastGsi?.MatchId;
        Draft = new DraftState(allies, enemies, CurrentPosition);
        LastDraftAnalysis = Analyzer.Analyze(Draft);
        RecomputeBuild();
        RaiseChanged();
    }

    public void RequestManualScan() => _manualScanRequested = true;

    public void StartManualTimer()
    {
        _manualTimerStartUtc = DateTime.UtcNow;
        Reminders.Reset();
        RaiseChanged();
    }

    public void StopManualTimer()
    {
        _manualTimerStartUtc = null;
        Reminders.Dismiss();
        RaiseChanged();
    }

    public void ReloadDatabase()
    {
        Database = GameDatabase.Load(AppPaths.DataOverrides, Settings.Current.Data.AutoUpdateData ? RemoteData.UsableRemoteDir(AppPaths.DataRemote) : null);
        RebuildEngines();
        CurrentHero = CurrentHero == null ? null : Database.FindHero(CurrentHero.Key);
        RecomputeBuild();
        RaiseChanged();
    }

    public void RebuildEngines()
    {
        Analyzer = new DraftAnalyzer(Database, Stats.HeroesCached > 0 ? Stats : NoStatsProvider.Instance, Settings.Current.Data.MinGamesForStats);
        Trainer = new DraftTrainer(Database, new DraftAnalyzer(Database, Stats.HeroesCached > 0 ? Stats : NoStatsProvider.Instance, Settings.Current.Data.MinGamesForStats));
        ApplyPool();
        Builds = new ItemBuildEngine(Database);
        Advisor = new RoleAdvisor(Database);
        if (Draft.Allies.Count + Draft.Enemies.Count > 0) LastDraftAnalysis = Analyzer.Analyze(Draft);
    }

    /// <summary>Учитывать ли героев противника в предметах (только Training).</summary>
    public bool EnemyAwareItems => ComplianceGate.IsAllowed(Feature.EnemyAwareItemAdvice, Settings.Current.UsageContext);

    public void RecomputeBuild()
    {
        CurrentBuild = CurrentHero == null
            ? null
            : PersonalBuildResolver.Resolve(PersonalBuildResolver.Active(Settings.Current, CurrentHero, CurrentPosition),
                CurrentHero, CurrentPosition, Database)
              ?? Builds.Build(CurrentHero, CurrentPosition, EnemyAwareItems ? Draft.Enemies : null);
    }

    private void ApplySettings()
    {
        var s = Settings.Current;
        Roles.SetDefault(s.Position);
        Loc.Instance.Language = s.Language;
        Reminders.ApplySettings(s.Notifications);
        ApplyPool();
        if (s.Overlay.ToggleHotkey != _lastToggleHotkey || s.Overlay.EditHotkey != _lastEditHotkey || s.Overlay.DetailsHotkey != _lastDetailsHotkey
            || s.Overlay.RoshanHotkey != _lastRoshanHotkey)
        {
            _lastRoshanHotkey = s.Overlay.RoshanHotkey;
            _lastDetailsHotkey = s.Overlay.DetailsHotkey;
            _lastToggleHotkey = s.Overlay.ToggleHotkey;
            _lastEditHotkey = s.Overlay.EditHotkey;
            // перерегистрация вне обработчика текущего сообщения
            _dispatcher.InvokeAsync(() => Overlay.RegisterHotkeys());
        }
        RestartGsiIfNeeded(force: false);
        if (Draft.UserPosition != CurrentPosition) Draft = Draft with { UserPosition = CurrentPosition };
        if (Draft.Allies.Count + Draft.Enemies.Count > 0) LastDraftAnalysis = Analyzer.Analyze(Draft);
        RecomputeBuild();
        UpdateAutoDraftSession();
        RecomputeAutoDraft();
        RefreshOverlay();
        Overlay.Update();
        RaiseChanged();
    }

    private void RestartGsiIfNeeded(bool force)
    {
        var g = Settings.Current.Gsi;
        if (!force && g.Enabled == _lastGsiEnabled && g.Port == _lastGsiPort && g.Token == _lastGsiToken) return;
        _lastGsiEnabled = g.Enabled;
        _lastGsiPort = g.Port;
        _lastGsiToken = g.Token;
        Gsi?.Dispose();
        Gsi = null;
        if (!g.Enabled) return;
        Gsi = new GsiServer(g.Port, g.Token);
        Gsi.SnapshotReceived += (_, snap) => _dispatcher.InvokeAsync(() => OnGsi(snap));
        Gsi.Start();
    }

    // ------------------------------------------------------------------ оверлей

    public void RefreshOverlay()
    {
        var L = Loc.Instance;
        var s = Settings.Current;
        var vm = OverlayVm;
        string role = L.Get("role_short_" + (int)CurrentPosition);
        vm.SubHeader = LastGsi?.ClockTime is int clk && LastGsi.IsInProgress
            ? $"{(clk < 0 ? "-" : "")}{Math.Abs(clk) / 60}:{Math.Abs(clk) % 60:00}"
            : L.Get(s.UsageContext == UsageContext.Training ? "ctx_training_short" : "ctx_reference_short").ToUpperInvariant();

        bool trainingDraft = ComplianceGate.IsAllowed(Feature.AutoDraftRecognition, s.UsageContext);
        bool inDraft = AutoDraftLive;
        vm.DraftMode = inDraft;

        if (inDraft)
        {
            vm.Header = (role + " / " + L.Get("auto_counters")).ToUpperInvariant();
            vm.SubHeader = L.Get("auto_live");
            vm.MainTitle = AutoDraftAnalysis?.Top.Count > 0 ? L.Get("auto_top") : L.Get("auto_wait_picks");
            vm.MainDetail = AutoDraftStatus;
            vm.Section1Title = AutoDraftAnalysis?.Top.Count > 0 ? L.Get("auto_why") : "";
            vm.ReplaceRows(vm.Section1, (AutoDraftAnalysis?.Top ?? Array.Empty<HeroRecommendation>()).Select(r =>
                new OverlayRow(r.Hero.Name,
                    string.Join("; ", r.Positives.Where(x => x.Kind is ReasonKind.Counter or ReasonKind.Stats).Take(2).Select(x => x.Text(L.Language))),
                    L.ConfidenceName(r.Confidence)) { Icon = HeroArt.Portrait(r.Hero.Key) }));
            vm.HeroPortrait = AutoDraftAnalysis?.Top.FirstOrDefault() is HeroRecommendation best ? HeroArt.Portrait(best.Hero.Key) : null;
            vm.GoldProgress = -1;
            var notes = AutoEnemies.Select(h => (Hero: h, Note: NoteFor(h))).Where(x => x.Note != null).ToList();
            vm.Section2Title = notes.Count > 0 ? L.Get("notes_overlay") : "";
            var draftRows = notes.Select(x => new OverlayRow(x.Hero.Name, Short(x.Note!, 140))).ToList();
            if (LastGsi?.GameState == "DOTA_GAMERULES_STATE_STRATEGY_TIME" && CurrentBriefing != null)
            {
                // выбор окончен — вместо пустого места показываем сводку драфта
                vm.Section2Title = L.Get("brief_title");
                draftRows = draftRows.Concat(BriefingRows(3).Select(r => new OverlayRow(r.Title, r.Text))).ToList();
            }
            vm.ReplaceRows(vm.Section2, draftRows);
            if (AutoDraftAnalysis?.Top.Count == 0 && AutoEnemies.Count > 0)
            { vm.MainTitle = L.Get("auto_no_counter"); vm.MainDetail = L.Get("auto_no_counter_hint"); }
        }
        else if (s.Automation.AutoOverlayModes && GsiFresh && LastGsi!.IsPostGame && FillPostGameMode(vm, role)) { }
        else if (s.Automation.AutoOverlayModes && s.Automation.ShowBuild && GsiFresh && LastGsi!.IsInProgress && LastGsi.HeroAlive == false
                 && CurrentHero != null && CurrentBuild != null)
            FillDeadMode(vm, role, LastGsi);
        else if (CurrentHero != null && CurrentBuild != null)
        {
            bool showBuild = s.Automation.ShowBuild;
            vm.Header = (role + " / " + L.Get(showBuild ? "sec_build" : "sec_assistant")).ToUpperInvariant();
            var owned = LastGsi?.InventoryItems;
            var next = CurrentBuild.NextItem(owned, LastGsi?.ClockTime);
            vm.MainTitle = next != null
                ? $"{CurrentHero.DisplayName(L.Language)} → {next.Item.DisplayName(L.Language)}"
                : CurrentHero.DisplayName(L.Language);
            vm.MainDetail = next?.Explain(L.Language) ?? L.Get("ov_build_done");
            if (next != null && GoldLine(next.Item.Key) is string goldLine) vm.MainDetail = goldLine + " · " + vm.MainDetail;
            vm.HeroPortrait = HeroArt.Portrait(CurrentHero.Key);
            vm.GoldProgress = next != null ? GoldProgress(next.Item.Key) : -1;

            var phase = PhaseForClock(LastGsi?.ClockTime);
            vm.Section1Title = L.PhaseName(phase) + (CurrentBuild.EnemyAware ? "" : " · " + L.Get("ov_reference_build"));
            vm.ReplaceRows(vm.Section1, CurrentBuild[phase].Concat(CurrentBuild[BuildPhase.Situational].Take(2)).Take(5).Select(p =>
                new OverlayRow(p.Item.DisplayName(L.Language), p.Explain(L.Language),
                    owned != null && owned.Contains(p.Item.Key) ? "✓" : Costs.Cost(p.Item.Key)?.ToString() ?? "") { Icon = HeroArt.ItemIcon(p.Item.Key) }));
            if (!showBuild)
            {
                // сборка выключена в настройках: только герой и советы, без предметов
                vm.MainTitle = CurrentHero.DisplayName(L.Language);
                vm.MainDetail = L.PositionName(CurrentPosition);
                vm.GoldProgress = -1;
                vm.Section1Title = "";
                vm.Section1.Clear();
            }
            var tips = Advisor.HeroTips(CurrentHero, CurrentPosition).Take(2)
                .Concat(Advisor.TipsFor(CurrentPosition).Take(1).Select(t => new Reason(ReasonKind.Info, 0, t.Ru, t.En)))
                .ToList();
            vm.Section2Title = L.Get("ov_tips");
            var rows = tips.Select(t => new OverlayRow("•", t.Text(L.Language))).ToList();
            if (NoteFor(CurrentHero) is string myNote) rows.Insert(0, new OverlayRow(L.Get("notes_mine"), Short(myNote, 160)));
            if (BriefingPhase)
            {
                vm.Section2Title = L.Get("brief_title");
                rows = BriefingRows().Select(r => new OverlayRow(r.Title, r.Text)).ToList();
            }
            else if (LanePhase && LaneRows() is { Count: > 0 } laneRows)
            {
                vm.Section2Title = L.Get("lane_title");
                rows = laneRows;
            }
            vm.ReplaceRows(vm.Section2, rows);
        }
        else
        {
            vm.Header = (role + " / " + L.Get("sec_assistant")).ToUpperInvariant();
            vm.MainTitle = L.Get("ov_pick_hero");
            vm.HeroPortrait = null;
            vm.GoldProgress = -1;
            vm.MainDetail = L.Get(trainingDraft ? "ov_pick_hero_hint_training" : "ov_pick_hero_hint");
            vm.Section1Title = "";
            vm.Section2Title = "";
            vm.Section1.Clear();
            vm.Section2.Clear();
        }

        vm.TimersLine = BuildTimersLine();
        vm.TimersUrgent = TimersUrgentNow();
        vm.Animate = !s.ReduceMotion;

        var gsiState = LastGsi != null && DateTime.UtcNow - LastGsi.ReceivedUtc < TimeSpan.FromSeconds(40)
            ? L.Get("gsi_connected") : L.Get("gsi_waiting");
        vm.Footer = $"GSI · {gsiState}".ToUpperInvariant();
        vm.HideHint = (s.Overlay.ToggleHotkey + " " + L.Get("ov_key_hide")).ToUpperInvariant();
        vm.EditHint = L.Format("ov_edit_hint", s.Overlay.EditHotkey)
            + (Overlay.CurrentLayoutKey is string lk ? " · " + L.Format("ov_edit_res", Roshiq.Core.Layout.LayoutMemory.Pretty(lk)) : "");
        vm.DetailsHint = vm.DraftMode ? "CTRL+SHIFT+R · " + L.Get("auto_role_key").ToUpperInvariant() : (s.Overlay.DetailsHotkey + " " + L.Get(s.Overlay.Mode == OverlayMode.Expanded ? "ov_key_less" : "ov_key_more")).ToUpperInvariant();
    }

    // ------------------------------------------------------------------ линия, пики силы, мета-сборки

    private string? _draftMatchId;
    private readonly HashSet<string> _spikesShown = new(StringComparer.Ordinal);
    private readonly HashSet<string> _spikeItems = new(StringComparer.OrdinalIgnoreCase);
    private bool _spikeSeeded;
    private IReadOnlyDictionary<int, string>? _itemIdMap;

    /// <summary>Драфт относится к текущему матчу (а не остался от прошлого).</summary>
    private bool DraftIsCurrent => LastGsi?.MatchId == null || _draftMatchId == LastGsi.MatchId;

    /// <summary>Советы на линию против вероятных соперников (от трёх видимых врагов).</summary>
    public IReadOnlyList<LaneOpponentAdvice> LaneAdvice =>
        CurrentBriefing is { } b && DraftIsCurrent
            ? LaneAdvisor.Advise(b.LaneOpponents, Database.LaneTips, Analyzer.PersonalAgainst)
            : Array.Empty<LaneOpponentAdvice>();

    /// <summary>Сравнение составов по фазам (нужно хотя бы 3 союзника и 3 врага).</summary>
    public TeamTiming? CurrentTiming
    {
        get
        {
            if (!DraftIsCurrent) return null;
            var allies = Draft.Allies.Concat(CurrentHero != null && !Draft.Allies.Any(h => h.Id == CurrentHero.Id) ? new[] { CurrentHero } : Array.Empty<Hero>()).ToList();
            var enemies = BriefingEnemies;
            return allies.Count >= 3 && enemies.Count >= 3 ? PowerSpikes.Compare(allies, enemies) : null;
        }
    }

    /// <summary>Первые 8 минут игры — время советов на линию.</summary>
    private bool LanePhase => Settings.Current.Automation.LaneTips && GsiFresh && LastGsi!.IsInProgress && LastGsi.ClockTime is int c && c is >= -90 and < 480;

    private List<OverlayRow> LaneRows()
    {
        var L = Loc.Instance;
        var rows = new List<OverlayRow>();
        foreach (var a in LaneAdvice)
        {
            var parts = a.Tips.Select(t => L.Language == Language.Russian ? t.Ru : t.En).ToList();
            if (a.Personal(L.Language) is { Length: > 0 } personal) parts.Add(personal);
            if (NoteFor(a.Hero) is string note) parts.Insert(0, Short(note, 100));
            if (parts.Count > 0) rows.Add(new OverlayRow(a.Hero.DisplayName(L.Language), string.Join(" ", parts.Take(2))) { Icon = HeroArt.Portrait(a.Hero.Key) });
        }
        return rows;
    }

    /// <summary>Уведомления: совет на линию на старте, подсказки по фазам составов и пики силы по предметам.</summary>
    /// <summary>Совет на линию уместен только в самом начале игры.</summary>
    private const int LaneTipUntil = 150;

    /// <summary>Игровое время того же матча по последнему снимку GSI; null — матч другой или не идёт.</summary>
    private int? LiveClock(string? matchId) =>
        LastGsi is { IsInProgress: true, ClockTime: int c } g && g.MatchId == matchId ? c : null;

    private void TickGameInsights(GameStateSnapshot snap)
    {
        if (!snap.IsInProgress || snap.ClockTime is not int clock) return;
        var a = Settings.Current.Automation;
        var L = Loc.Instance;

        // советы привязаны к моменту игры: если показ задержался (пауза, смерть, напоминание саппорту, пропуск GSI),
        // перед показом проверяем, что это тот же матч и момент ещё не прошёл
        var matchId = snap.MatchId;
        if (a.LaneTips && clock is >= 0 and < 40 && _spikesShown.Add("lane_start"))
        {
            var advice = LaneAdvice.Where(x => x.Tips.Count > 0).Take(2).ToList();
            if (advice.Count > 0)
            {
                ShowEventToast(L.Get("lane_title").ToUpperInvariant(), string.Join("  ", advice.Select(x =>
                    x.Hero.DisplayName(L.Language) + ": " + (L.Language == Language.Russian ? x.Tips[0].Ru : x.Tips[0].En))),
                    kind: "lane", ttlSec: 90, inGame: true, stillValid: () => LiveClock(matchId) is int c && c < LaneTipUntil);
            }
        }

        if (!a.PowerSpikes) return;
        if (CurrentTiming is { } timing && PowerSpikes.ClockReminder(timing, clock) is { } r && _spikesShown.Add(r.Key))
        {
            int until = r.Until;
            ShowEventToast(L.Get("spike_header").ToUpperInvariant(), L.Language == Language.Russian ? r.Ru : r.En,
                kind: "spike", key: "spike:" + r.Key, ttlSec: 60, inGame: true, stillValid: () => LiveClock(matchId) is int c && c < until);
        }

        var owned = snap.InventoryItems;
        if (owned == null) return;
        if (!_spikeSeeded)
        {
            _spikeSeeded = true;
            // ROSHIQ запустили посреди матча — уже купленное не объявляем
            if (clock > 120) { foreach (var k in owned) _spikeItems.Add(k); return; }
        }
        var core = CurrentBuild == null ? Array.Empty<string>()
            : CurrentBuild[BuildPhase.Mid].Concat(CurrentBuild[BuildPhase.Late]).Select(p => p.Item.Key).ToArray();
        foreach (var key in PowerSpikes.NewSpikes(owned, _spikeItems, Costs.Cost, core).Where(_ => a.ShowBuild))
        {
            var name = Database.FindItem(key)?.DisplayName(L.Language) ?? key;
            var item = key;
            // доходный предмет — нейтрально, остальные — без приказа драться; предмет продан или отдан — совет снимается
            ShowEventToast(L.Get("spike_item_header").ToUpperInvariant(), L.Format(PowerSpikes.ItemMessageKey(key), name),
                kind: "spike_item", key: "spike_item:" + key, ttlSec: 45, inGame: true,
                stillValid: () => LiveClock(matchId) != null && LastGsi?.InventoryItems?.Contains(item, StringComparer.OrdinalIgnoreCase) == true);
            break; // за раз — одно уведомление
        }
    }

    /// <summary>Загрузить мета-сборку героя из OpenDota (кэш на 3 дня). Возвращает сборку или текст ошибки.</summary>
    public async Task<(MetaBuild? Build, string? Message)> LoadMetaBuildAsync(Hero hero, bool force)
    {
        if (!force && MetaBuilds.Get(hero.Id) is { } cached && !MetaBuilds.IsStale(hero.Id)) return (cached, null);
        var d = Settings.Current.Data;
        if (!d.AllowOnlineStats) return (MetaBuilds.Get(hero.Id), Loc.T("steam_need_network"));
        try
        {
            using var client = new OpenDotaClient(d.OpenDotaApiKey);
            _itemIdMap ??= await client.GetItemIdMapAsync(CancellationToken.None);
            var raw = await client.GetItemPopularityAsync(hero.Id, CancellationToken.None);
            var phases = new Dictionary<BuildPhase, IReadOnlyDictionary<int, int>>();
            foreach (var (k, v) in raw)
                if (MetaBuildCache.PhaseOf(k) is BuildPhase p) phases[p] = v;
            var build = MetaBuildBuilder.Build(hero.Id, phases, _itemIdMap, key => Database.FindItem(key) is { CatalogOnly: false });
            if (build.TotalItems == 0) return (MetaBuilds.Get(hero.Id), Loc.T("meta_empty"));
            MetaBuilds.Set(build);
            return (build, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Мета-сборка {hero.Key}: {ex.Message}");
            return (MetaBuilds.Get(hero.Id), Loc.T("meta_failed") + " " + ex.Message);
        }
    }

    /// <summary>Личная сборка из меты — дальше её можно править в редакторе.</summary>
    public PersonalBuild PersonalFromMeta(MetaBuild meta, Hero hero, Position position) => new()
    {
        HeroKey = hero.Key,
        Position = position,
        Name = Loc.Instance.Format("meta_build_name", meta.FetchedUtc.ToLocalTime().ToString("dd.MM")),
        Notes = Loc.T("meta_build_note"),
        Entries = meta.Phases.OrderBy(p => p.Key).SelectMany(p => p.Value.Take(p.Key == BuildPhase.Start ? 6 : 4)
            .Select(i => new PersonalBuildEntry { Phase = p.Key, ItemKey = i.ItemKey })).ToList()
    };

    // ------------------------------------------------------------------ автоматика: игра, роль, импорт, режимы оверлея

    /// <summary>A manual choice applies to this match only; outside a match it changes the saved usual role.</summary>
    public void SelectPosition(Position position)
    {
        if (!Roles.Active) { Settings.Update(s => s.Position = position); return; }
        if (Roles.Source == MatchRoleSource.Manual && CurrentPosition == position) return;
        Roles.SelectManual(position);
        _roleDetector.AcceptAssignedRole();
        OnEffectiveRoleChanged();
    }

    private void OnEffectiveRoleChanged()
    {
        if (!Roles.Active || Roles.Source != MatchRoleSource.Default) Roshiq.App.Views.RolePromptWindow.CloseIfOpen();
        if (_recorder.Current != null && Roles.Active) _recorder.Current.Position = CurrentPosition;
        if (Draft.UserPosition != CurrentPosition) Draft = Draft with { UserPosition = CurrentPosition };
        if (Draft.Allies.Count + Draft.Enemies.Count > 0) LastDraftAnalysis = Analyzer.Analyze(Draft);
        RecomputeBuild();
        RecomputeAutoDraft();
        RefreshOverlay();
        RaiseChanged();
    }

    /// <summary>One checked role in Steam's ranked queue config is known before lane play starts.</summary>
    private void TickRankedRoleSelection(GameStateSnapshot snap)
    {
        if (!snap.IsHeroSelection)
        {
            _queueRoleApplied = false;
            _queueHasMultiple = false;
            Roshiq.App.Views.RolePromptWindow.CloseIfOpen();
            return;
        }
        if (!Settings.Current.Automation.AutoRole || _queueRoleApplied || Roles.Source == MatchRoleSource.Manual || DateTime.UtcNow - _queueRoleLastCheckUtc < TimeSpan.FromSeconds(5)) return;
        _queueRoleLastCheckUtc = DateTime.UtcNow;
        var steamRoot = SteamLink.SteamPath();
        if (steamRoot == null) return;
        var account = SteamIds.ToAccountId(snap.SteamId)
            ?? SteamLink.LocalUsers().FirstOrDefault(u => u.MostRecent)?.AccountId
            ?? SteamIds.ToAccountId(Settings.Current.Data.SteamAccountId);
        if (account is not long accountId) return;
        var checkedRoles = RankedRoleSelection.ReadChecked(steamRoot, accountId);
        if (checkedRoles.Count > 1)
        {
            _queueHasMultiple = true;
            if (!_rolePromptShown && Roles.Source == MatchRoleSource.Default &&
                DateTime.UtcNow - _roleSessionStartedUtc >= TimeSpan.FromSeconds(10))
            {
                _rolePromptShown = true;
                Roshiq.App.Views.RolePromptWindow.Open();
            }
            return;
        }
        if (checkedRoles.Count != 1) return;
        var position = checkedRoles[0];

        _queueRoleApplied = true;
        Log.Info($"Роль из выбора рейтингового поиска: {position}");
        if (!Roles.ApplyAutomatic(position, MatchRoleSource.Queue)) return;
        _roleDetector.AcceptAssignedRole();
        OnEffectiveRoleChanged();
        var L = Loc.Instance;
        ShowEventToast(L.Get("role_auto_header").ToUpperInvariant(),
            L.Format("role_queue_changed", L.PositionName(position)), kind: "role", ttlSec: 60);
    }

    private bool GsiFresh => LastGsi != null && DateTime.UtcNow - LastGsi.ReceivedUtc < TimeSpan.FromSeconds(10);

    /// <summary>Запуск/закрытие Доты по окну игры (тот же поиск окна, что и у оверлея).</summary>
    private void WatchGame()
    {
        if (!Settings.Current.Automation.WatchGame) return;
        if (_gameWatch.Observe(Overlay.Game.Found) is not bool started) return;
        if (started)
        {
            MatchSavedSinceGameStart = false;
            Log.Info("Dota 2 запущена");
            GameLaunched?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Log.Info("Dota 2 закрыта");
            if (Roles.Leave()) OnEffectiveRoleChanged();
            GameClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Автоопределение роли в начале матча (линия по координатам GSI + герой + история + добивания).</summary>
    private void TickRoleDetection(GameStateSnapshot snap)
    {
        if (!Settings.Current.Automation.AutoRole || _roleDetector.Decided || !snap.IsInProgress || snap.ClockTime is not int clock || CurrentHero == null)
            return;
        _roleDetector.Observe(clock, snap.HeroX, snap.HeroY, snap.LastHits);
        var guess = _roleDetector.TryDecide(clock, CurrentHero, snap.TeamName, () => History.All(), CurrentPosition);
        if (guess == null) return;
        Log.Info($"Роль определена: {guess.Position} ({guess.Confidence:0.00}; {guess.ReasonRu})");
        if (guess.Confidence < LaneRoleDetector.MinConfidence) return;
        if (!Roles.ApplyAutomatic(guess.Position, MatchRoleSource.Lane, guess.ReasonRu)) return;
        OnEffectiveRoleChanged();
        var L = Loc.Instance;
        ShowEventToast(L.Get("role_auto_header").ToUpperInvariant(),
            L.Format("role_auto_changed", L.PositionName(guess.Position), guess.Reason(L.Language)), kind: "role", ttlSec: 60);
    }

    private async Task StartupImportAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        var d = Settings.Current.Data;
        if (d.SteamAccountId != null && (d.ProfileCheckedUtc == null || DateTime.UtcNow - d.ProfileCheckedUtc > TimeSpan.FromHours(12)))
            await RefreshSteamProfileAsync();
        await AutoImportOnceAsync(null);
    }

    // ------------------------------------------------------------------ Steam: привязка, профиль, статистика героев

    public long? SteamAccount => SteamIds.ToAccountId(Settings.Current.Data.SteamAccountId);

    /// <summary>Привязать аккаунт (Steam ID64 или account id). via: gsi / local / openid / manual.</summary>
    public void LinkSteam(string steamId, string via, string? name = null)
    {
        if (SteamIds.ToAccountId(steamId) is not long account) return;
        bool changed = SteamAccount != account;
        Settings.Update(s =>
        {
            s.Data.SteamAccountId = account.ToString(System.Globalization.CultureInfo.InvariantCulture);
            s.Data.SteamLinkedVia = via;
            if (changed)
            {
                s.Data.SteamName = name;
                s.Data.SteamAvatarUrl = null;
                s.Data.RankTier = null;
                s.Data.LeaderboardRank = null;
                s.Data.ProfileCheckedUtc = null;
                s.Data.PublicMatchData = null;
            }
            else if (name != null) s.Data.SteamName ??= name;
        });
        if (changed) PlayerHeroes.Clear();
        Log.Info($"Steam привязан ({via}): {account}");
        ApplyPool();
        SteamChanged?.Invoke(this, EventArgs.Empty);
        if (changed)
        {
            _ = RefreshSteamProfileAsync();
            _ = AutoImportOnceAsync(null);
        }
    }

    public void UnlinkSteam()
    {
        Settings.Update(s =>
        {
            s.Data.SteamAccountId = null;
            s.Data.SteamLinkedVia = null;
            s.Data.SteamName = null;
            s.Data.SteamAvatarUrl = null;
            s.Data.RankTier = null;
            s.Data.LeaderboardRank = null;
            s.Data.ProfileCheckedUtc = null;
            s.Data.PublicMatchData = null;
        });
        PlayerHeroes.Clear();
        ApplyPool();
        Log.Info("Steam отвязан");
        SteamChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Обновить ник, аватар, медаль и статистику по героям из OpenDota. Возвращает текст ошибки или null.</summary>
    public async Task<string?> RefreshSteamProfileAsync()
    {
        var d = Settings.Current.Data;
        if (SteamAccount is not long account) return Loc.T("steam_not_linked");
        if (!d.AllowOnlineStats) return Loc.T("steam_need_network");
        try
        {
            using var client = new OpenDotaClient(d.OpenDotaApiKey);
            var profile = await client.GetPlayerProfileAsync(account, CancellationToken.None);
            var heroes = await client.GetPlayerHeroesAsync(account, CancellationToken.None);
            if (SteamAccount != account) return null; // успели перепривязать
            PlayerHeroes.Replace(account, heroes, DateTime.UtcNow);
            Settings.Update(s =>
            {
                s.Data.ProfileCheckedUtc = DateTime.UtcNow;
                if (profile != null)
                {
                    s.Data.SteamName = profile.Name ?? s.Data.SteamName;
                    s.Data.SteamAvatarUrl = profile.AvatarUrl;
                    s.Data.RankTier = profile.RankTier;
                    s.Data.LeaderboardRank = profile.LeaderboardRank;
                }
                if (heroes.Count > 0) s.Data.PublicMatchData = true;
            });
            ApplyPool();
            Log.Info($"Профиль Steam обновлён: {profile?.Name}, героев со статистикой {heroes.Count}");
            SteamChanged?.Invoke(this, EventArgs.Empty);
            return profile == null ? Loc.T("steam_no_profile") : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Профиль OpenDota: " + ex.Message);
            return Loc.T("steam_refresh_failed") + " " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ разбор матча (OpenDota)

    private readonly HashSet<string> _reviewPending = new(StringComparer.Ordinal);

    private string? HeroNpc(int heroId) => Database.FindHero(heroId)?.Key is string key ? "npc_dota_hero_" + key : null;

    /// <summary>
    /// Разобрать матч: загрузить его из OpenDota, при необходимости попросить разобрать запись, посчитать нормы таймингов
    /// и сделать выводы. Возвращает разбор или текст, почему его пока нет.
    /// </summary>
    public async Task<(MatchReview? Review, string? Message)> ReviewMatchAsync(string matchId, Position? position, bool requestParse)
    {
        var d = Settings.Current.Data;
        if (SteamAccount is not long account) return (null, Loc.T("review_need_steam"));
        if (!d.AllowOnlineStats) return (null, Loc.T("steam_need_network"));
        if (!long.TryParse(matchId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id))
            return (null, Loc.T("review_no_id"));
        try
        {
            using var client = new OpenDotaClient(d.OpenDotaApiKey);
            var details = await client.GetMatchAsync(id, account, HeroNpc, CancellationToken.None);
            if (details == null) return (null, Loc.T("review_not_found"));
            if (!details.Parsed && requestParse)
            {
                await client.RequestParseAsync(id, CancellationToken.None);
                // итог без разбора всё равно сохраняем: сравнение с игроками на герое уже есть
                if (details.Benchmarks.Count == 0) return (null, Loc.T("review_parsing"));
            }
            var norms = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in MatchReviewBuilder.KeyPurchases(details, Costs.Cost))
            {
                var norm = ItemNorms.Get(details.HeroId, p.ItemKey, out bool stale);
                if (stale)
                {
                    try { norm = await client.GetItemTimingNormAsync(details.HeroId, p.ItemKey, CancellationToken.None); ItemNorms.Set(details.HeroId, p.ItemKey, norm); }
                    catch (Exception ex) { Log.Warn($"Норма тайминга {p.ItemKey}: {ex.Message}"); }
                }
                norms[p.ItemKey] = norm;
            }
            var review = MatchReviewBuilder.Build(details, position, norms,
                key => Database.FindItem(key) is { } item ? (item.NameRu, item.Name) : (key, key),
                hid => Database.FindHero(hid) is { } h ? (h.NameRu, h.Name) : ("?", "?"),
                Costs.Cost);
            Reviews.Save(review);
            Log.Info($"Разбор матча {matchId}: выводов {review.Insights.Count}, разобран: {review.Parsed}");
            return (review, review.Parsed ? null : Loc.T("review_parsing"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Разбор матча {matchId}: {ex.Message}");
            return (null, Loc.T("review_failed") + " " + ex.Message);
        }
    }

    /// <summary>После матча: попросить разбор записи и ждать его (через 4, 10 и 20 минут). Готово — уведомление.</summary>
    private async Task AutoReviewAsync(string matchId)
    {
        if (!Settings.Current.Automation.AutoReview || SteamAccount == null || !_reviewPending.Add(matchId)) return;
        try
        {
            var position = History.All().FirstOrDefault(m => m.MatchId == matchId)?.Position;
            await ReviewMatchAsync(matchId, position, requestParse: true);
            foreach (var minutes in new[] { 4, 10, 20 })
            {
                if (Reviews.Get(matchId) is { Parsed: true }) break;
                await Task.Delay(TimeSpan.FromMinutes(minutes));
                await ReviewMatchAsync(matchId, position, requestParse: false);
            }
            if (Reviews.Get(matchId) is { } review)
            {
                var L = Loc.Instance;
                var top = review.Problems.FirstOrDefault() ?? review.Good.FirstOrDefault();
                ShowEventToast(L.Get("review_ready").ToUpperInvariant(), top != null ? top.Text(L.Language) : L.Get("review_no_insights"),
                    priority: TipPriority.Background, kind: "review", ttlSec: 1800);
                RaiseChanged();
            }
        }
        finally { _reviewPending.Remove(matchId); }
    }

    /// <summary>Пул по вашей статистике: часто играете и выигрываете (ключи героев).</summary>
    public IReadOnlyList<string> SuggestPoolFromSteam() =>
        PlayerHeroes.SuggestPool().Select(Database.FindHero).Where(h => h != null).Select(h => h!.Key).ToList();

    /// <summary>Привязка из игры: Dota сама передаёт Steam ID игрока в GSI.</summary>
    private void TryLinkFromGsi(GameStateSnapshot snap)
    {
        if (Settings.Current.Data.SteamAccountId != null || string.IsNullOrEmpty(snap.SteamId)) return;
        if (SteamIds.ToAccountId(snap.SteamId) is null) return;
        LinkSteam(snap.SteamId!, "gsi", snap.PlayerName);
        ShowEventToast(Loc.T("steam_header").ToUpperInvariant(), Loc.Instance.Format("steam_linked_gsi", snap.PlayerName ?? snap.SteamId!),
            priority: TipPriority.Background, kind: "steam", ttlSec: 1800);
    }

    /// <summary>После матча: OpenDota обычно видит игру через несколько минут — пробуем через 2, 8 и 25 минут.</summary>
    private async Task AutoImportAfterMatchAsync(string? matchId)
    {
        if (string.IsNullOrEmpty(matchId) || !_importPending.Add(matchId)) return;
        try
        {
            foreach (var minutes in new[] { 2, 8, 25 })
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes));
                if (await AutoImportOnceAsync(matchId)) return;
            }
        }
        finally { _importPending.Remove(matchId); }
    }

    /// <summary>Подтянуть последние матчи из OpenDota. true — делать больше нечего (матч найден или импорт выключен).</summary>
    private async Task<bool> AutoImportOnceAsync(string? waitFor)
    {
        var s = Settings.Current;
        if (!s.Automation.AutoImport || !s.Data.AllowOnlineStats) return true;
        if (SteamIds.ToAccountId(s.Data.SteamAccountId) is not long account) return true;
        try
        {
            using var client = new OpenDotaClient(s.Data.OpenDotaApiKey);
            var rows = await client.GetRecentMatchesAsync(account, CancellationToken.None);
            int added = History.Import(rows).Added;
            bool? visible = rows.Count > 0 ? true : History.All().Any(m => m.Source == "GSI") ? false : null;
            if (visible != null && visible != s.Data.PublicMatchData)
            {
                Settings.Update(x => x.Data.PublicMatchData = visible);
                SteamChanged?.Invoke(this, EventArgs.Empty);
            }
            bool found = waitFor == null || rows.Any(r => r.MatchId.ToString(System.Globalization.CultureInfo.InvariantCulture) == waitFor);
            Log.Info($"Автоимпорт OpenDota: получено {rows.Count}, добавлено {added}" + (waitFor != null ? (found ? $", матч {waitFor} найден" : $", матча {waitFor} пока нет") : ""));
            if (found && waitFor != null)
            {
                RaiseChanged();
                _ = AutoReviewAsync(waitFor);
            }
            return found;
        }
        catch (Exception ex)
        {
            Log.Warn("Автоимпорт OpenDota: " + ex.Message);
            return false;
        }
    }

    /// <summary>Оверлей «пока мёртв»: что купить, выкуп, склад и ближайшие события.</summary>
    private void FillDeadMode(OverlayViewModel vm, string role, GameStateSnapshot g)
    {
        var L = Loc.Instance;
        var owned = g.InventoryItems;
        var next = CurrentBuild!.NextItem(owned, g.ClockTime);
        vm.Header = (role + " / " + L.Get("dead_header") + (g.RespawnSeconds is int r && r > 0 ? " · " + GameTimers.Clock(r) : "")).ToUpperInvariant();
        vm.MainTitle = next != null ? L.Format("dead_buy", next.Item.DisplayName(L.Language)) : L.Get("ov_build_done");
        vm.MainDetail = next == null ? "" : GoldLine(next.Item.Key) ?? next.Explain(L.Language);
        vm.HeroPortrait = (next != null ? HeroArt.ItemIcon(next.Item.Key) : null) ?? HeroArt.Portrait(CurrentHero!.Key);
        vm.GoldProgress = next != null ? GoldProgress(next.Item.Key) : -1;

        var rows = new List<OverlayRow>();
        int gold = g.Gold ?? 0;
        if (g.BuybackCost is int bb && bb > 0)
        {
            string text = g.BuybackCooldown is int cd && cd > 0 ? L.Format("dead_bb_cd", GameTimers.Clock(cd))
                : gold >= bb ? L.Format("dead_bb_ready", bb) : L.Format("dead_bb_short", bb - gold);
            rows.Add(new OverlayRow(L.Get("dead_bb"), text));
            int lateFrom = Settings.Current.Alerts.BuybackFromMinute * 60;
            if (next != null && Costs.Cost(next.Item.Key) is int cost && gold >= cost && gold - cost < bb
                && g.BuybackCooldown is 0 && g.ClockTime >= lateFrom)
                rows.Add(new OverlayRow(L.Get("dead_keep_bb"), L.Format("dead_keep_bb_hint", bb)));
        }
        if (g.ItemSlots.Any(kv => kv.Key.StartsWith("stash", StringComparison.Ordinal) && kv.Value != "empty"))
            rows.Add(new OverlayRow(L.Get("dead_stash"), L.Get("dead_stash_hint")));
        vm.Section1Title = rows.Count > 0 ? L.Get("dead_status") : "";
        vm.ReplaceRows(vm.Section1, rows);

        var events = g.ClockTime is int clock && Settings.Current.Timers.Enabled
            ? GameTimers.Upcoming(Database.Timers, clock, TimerOn, 3).Select(e => new OverlayRow(TimerName(e.Rule), L.Format("dead_in", GameTimers.Clock(e.InSec)))).ToList()
            : new List<OverlayRow>();
        vm.Section2Title = events.Count > 0 ? L.Get("dead_events") : "";
        vm.ReplaceRows(vm.Section2, events);
    }

    /// <summary>Оверлей после матча: результат, разбор смертей и цель.</summary>
    private bool FillPostGameMode(OverlayViewModel vm, string role)
    {
        var m = _lastSaved;
        if (m == null || m.MatchId != LastGsi?.MatchId) return false;
        var L = Loc.Instance;
        var hero = Database.FindHero(m.HeroId);
        vm.Header = (role + " / " + L.Get("post_header")).ToUpperInvariant();
        string result = L.Get(m.Result switch { MatchResult.Win => "hist_win", MatchResult.Loss => "hist_loss", _ => "hist_unknown" });
        vm.MainTitle = (hero?.DisplayName(L.Language) ?? "") + (hero != null ? " · " : "") + result;
        vm.MainDetail = L.Format("post_line", m.Kills, m.Deaths, m.Assists, m.Gpm, m.LastHits);
        vm.HeroPortrait = hero != null ? HeroArt.Portrait(hero.Key) : null;
        vm.GoldProgress = -1;

        var rows = new List<OverlayRow>();
        var d = DeathAnalysis.Analyze(m);
        if (m.Deaths == 0) rows.Add(new OverlayRow("✓", L.Get("deaths_none")));
        else if (d == null || d.Total == 0) rows.Add(new OverlayRow("?", L.Get("deaths_not_recorded")));
        else
        {
            if (d.WithLotsOfGold > 0) rows.Add(new OverlayRow("×" + d.WithLotsOfGold, L.Format("deaths_gold", d.WithLotsOfGold, DeathAnalysis.LotsOfGold)));
            if (d.WithoutTeleport > 0) rows.Add(new OverlayRow("×" + d.WithoutTeleport, L.Get("deaths_no_tp")));
            if (d.LateWithoutBuyback > 0) rows.Add(new OverlayRow("×" + d.LateWithoutBuyback, L.Get("deaths_no_bb")));
            if (rows.Count == 0) rows.Add(new OverlayRow("✓", L.Format("deaths_clean", d.AvgGoldAtRisk)));
        }
        vm.Section1Title = L.Get("post_deaths");
        vm.ReplaceRows(vm.Section1, rows);

        var tail = new List<OverlayRow>();
        if (Reviews.Get(m.MatchId) is { } review)
            foreach (var ins in review.Top) tail.Add(new OverlayRow(ins.Severity < 0 ? "!" : "✓", ins.Text(L.Language)));
        if (Goals.Active is CoachGoal goal) tail.Add(new OverlayRow(L.Get("goal_header"), GoalText(goal)));
        tail.Add(new OverlayRow("→", L.Get("post_open_history")));
        vm.Section2Title = "";
        vm.ReplaceRows(vm.Section2, tail);
        return true;
    }

    // ------------------------------------------------------------------ матч: таймеры, Рошан, подсказки, история

    /// <summary>Игровые часы: из GSI, иначе ручной таймер матча. null — матч не идёт.</summary>
    public int? GameClock
    {
        get
        {
            if (LastGsi != null && DateTime.UtcNow - LastGsi.ReceivedUtc < TimeSpan.FromSeconds(10) && LastGsi.IsInProgress && LastGsi.ClockTime is int c)
                return c;
            if (_manualTimerStartUtc != null) return (int)(DateTime.UtcNow - _manualTimerStartUtc.Value).TotalSeconds;
            return null;
        }
    }

    public bool TimerOn(string key)
    {
        var s = Settings.Current;
        if (!s.Timers.IsOn(key)) return false;
        var rule = Database.Timers.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
        return rule == null || rule.AppliesTo(CurrentPosition);
    }

    private string TimerName(TimerRule rule)
    {
        var L = Loc.Instance;
        if (rule.Key == "daynight" && LastGsi?.Daytime is bool day) return L.Get(day ? "timer_night" : "timer_day");
        return rule.Name(L.Language);
    }

    private void TickMatchEvents(int clock)
    {
        var t = Settings.Current.Timers;
        if (!t.Enabled) return;
        var L = Loc.Instance;
        foreach (var e in _timerAnnouncer.Due(Database.Timers, clock, TimerOn))
            if (t.Toasts) ShowEventToast(L.Get("timer_header").ToUpperInvariant(), L.Format("timer_soon", TimerName(e.Rule), e.InSec),
                priority: TipPriority.Urgent, kind: "timer", key: "timer:" + TimerName(e.Rule), ttlSec: Math.Max(5, e.InSec), inGame: true, allowWhenDead: true);
        foreach (var key in Roshan.Due(clock))
            ShowEventToast(L.Get("rosh_header").ToUpperInvariant(), L.Get(key), priority: TipPriority.Urgent, kind: "rosh", key: "rosh:" + key,
                ttlSec: 30, inGame: true, allowWhenDead: true);
    }

    private void TickAlerts(int clock)
    {
        var g = LastGsi!;
        var state = new InventoryState(clock, g.HeroAlive != false, g.Gold, g.GoldUnreliable, g.ItemSlots, CurrentPosition,
            g.BuybackCost, g.BuybackCooldown, NextBuildItemCost(g));
        int tpCost = Costs.Cost("item_tpscroll") ?? 100;
        _alertActive = MatchAlerts.Conditions(state, Settings.Current.Alerts, tpCost);
        var key = Alerts.Tick(state, Settings.Current.Alerts, tpCost);
        if (key == null) return;
        var L = Loc.Instance;
        var text = key switch
        {
            MatchAlerts.UnspentGold => L.Format(key, g.GoldUnreliable ?? g.Gold ?? 0),
            MatchAlerts.NoBuyback => L.Format(key, (g.BuybackCost ?? 0) - (g.Gold ?? 0)),
            _ => L.Get(key)
        };
        // срочно — то, что стоит жизни или выкупа; остальное — обычным порядком. Снимается, как только условие ушло.
        var urgent = key is MatchAlerts.NoTeleport or MatchAlerts.NoBuyback;
        ShowEventToast(L.Get("alert_header").ToUpperInvariant(), text, warning: true, priority: urgent ? TipPriority.Urgent : TipPriority.Normal,
            kind: key, ttlSec: 45, inGame: true, stillValid: () => _alertActive.Contains(key), tag: key);
    }

    /// <summary>Цена следующего ещё не купленного предмета сборки (середина и поздняя игра) — чтобы не ругать за «лишнее золото», когда игрок копит.</summary>
    private int? NextBuildItemCost(GameStateSnapshot g)
    {
        if (!Settings.Current.Automation.ShowBuild || CurrentBuild == null || g.InventoryItems is not { } owned) return null;
        var next = CurrentBuild[BuildPhase.Mid].Concat(CurrentBuild[BuildPhase.Late]).Select(p => p.Item.Key)
            .FirstOrDefault(k => !owned.Contains(k));
        return next == null ? null : Costs.Cost(next);
    }

    /// <summary>Применить профиль оверлея (режим, положение, масштаб, прозрачность) — из трея.</summary>
    public void ApplyOverlayProfile(string id)
    {
        string? name = null;
        Settings.Update(x =>
        {
            var p = x.OverlayProfiles.FirstOrDefault(pp => pp.Id == id);
            if (p == null) return;
            p.ApplyTo(x.Overlay);
            x.ActiveOverlayProfile = id;
            name = p.Name;
        });
        if (name != null) ShowEventToast("ROSHIQ", Loc.Instance.Format("prof_applied", name), priority: TipPriority.Urgent, kind: "profile", ttlSec: 10);
    }

    /// <summary>Сторож уже предупредил в этом запуске Dota — ждём, когда данные всё-таки пойдут, чтобы записать это в журнал.</summary>
    private bool _gsiWarned;

    private void TickGsiWatchdog()
    {
        var problem = GsiWatch.Tick(Overlay.Game.Found, Settings.Current.Gsi.Enabled, Gsi?.LastPacketUtc, Gsi?.PacketsRejected ?? 0, DateTime.UtcNow);
        if (!Overlay.Game.Found) _gsiWarned = false;
        else if (_gsiWarned && GsiWatch.GameSinceUtc is DateTime since && Gsi?.LastPacketUtc is DateTime packet && packet >= since)
        {
            // для разбора: предупреждение было, но связь появилась — через сколько после запуска игры
            _gsiWarned = false;
            Log.Info($"Сторож GSI: данные пошли через {(packet - since).TotalSeconds:0} с после запуска Dota 2");
        }
        if (problem == GsiProblem.None) return;
        _gsiWarned = true;
        Log.Warn("Сторож GSI: Dota 2 запущена, а данных нет (" + problem + ")");
        var L = Loc.Instance;
        ShowEventToast(L.Get("gsi_watch_header").ToUpperInvariant(),
            L.Get(problem == GsiProblem.BadToken ? "gsi_watch_token" : Settings.Current.Gsi.Enabled ? "gsi_watch_nodata" : "gsi_watch_off"), warning: true,
            priority: TipPriority.Urgent, kind: "gsi_watch", ttlSec: 120);
        GsiProblemDetected?.Invoke(this, problem);
    }

    /// <summary>Минута без сбоя — запуск удачный, счётчик сбоев обнуляется.</summary>
    private async Task MarkStableLaterAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), _backgroundCts.Token);
            CrashGuard.MarkStable(AppPaths.StartupGuard);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Поставить уведомление в очередь диспетчера советов. Диспетчер сам выберет момент: не перебьёт напоминание саппорту,
    /// срочное покажет первым, служебное отложит до конца матча, неактуальное снимет без показа.
    /// </summary>
    public bool ShowEventToast(string header, string text, bool warning = false, TipPriority priority = TipPriority.Normal,
        string kind = "event", int ttlSec = 30, bool inGame = false, Func<bool>? stillValid = null, object? tag = null, string? key = null,
        bool allowWhenDead = false)
    {
        var tip = new Tip
        {
            Kind = kind, Key = key, Header = header, Text = text, Warning = warning, Priority = priority,
            Ttl = TimeSpan.FromSeconds(ttlSec), InGameOnly = inGame, StillValid = stillValid, Tag = tag, AllowWhenDead = allowWhenDead
        };
        if (inGame && !Settings.Current.AdviceDelivery.ShowDuringMatch(kind))
        {
            var deferred = new DeferredMatchTip { Kind = kind, Text = text, Clock = Math.Max(0, LastGsi?.ClockTime ?? 0) };
            if (_recorder.Current is MatchRecord current) { FlushDeferred(current); DeferredTipList.Add(current.DeferredTips, deferred); }
            else DeferredTipList.Add(_deferredEarly, deferred);
            OnTipOutcome(tip, TipOutcome.Deferred);
            return false;
        }
        Tips.Enqueue(tip, DateTime.UtcNow);
        PumpTips();
        return true;
    }

    /// <summary>Перенести в запись матча советы, отложенные до её появления.</summary>
    private void FlushDeferred(MatchRecord record)
    {
        foreach (var t in _deferredEarly) DeferredTipList.Add(record.DeferredTips, t);
        _deferredEarly.Clear();
    }

    /// <summary>Раз в секунду (и сразу при новом совете): выбрать совет и показать его в оверлее.</summary>
    private void PumpTips()
    {
        var g = LastGsi;
        bool fresh = g != null && DateTime.UtcNow - g.ReceivedUtc < TimeSpan.FromSeconds(5);
        var ctx = new TipContext(
            Blocked: Reminders.Active != null,
            InGame: fresh && g!.IsInProgress,
            Paused: g?.Paused == true,
            Stale: !fresh,
            Dead: g?.HeroAlive == false);
        var now = DateTime.UtcNow;
        if (Tips.Next(ctx, now) is not { } tip) return;
        bool shown = Overlay.ShowToast(tip.Header, tip.Text, tip.Warning);
        Tips.Report(tip, shown, now);
        if (shown) Sound.Play(tip.Warning ? SoundKind.Warning : SoundKind.Event);
    }

    /// <summary>
    /// Исход совета: в журнал (кроме обычных показов), в запись матча (для проверки пользы) и обратно в подсказки инвентаря —
    /// не показанная подсказка откатывается, чтобы не считаться проигнорированной.
    /// </summary>
    private void OnTipOutcome(Tip tip, TipOutcome outcome)
    {
        if (tip.Tag is string alertKey && outcome != TipOutcome.Shown)
        {
            if (outcome == TipOutcome.Dropped) Alerts.Unseen(alertKey);
            else Alerts.Retract(alertKey);
        }
        if (tip.InGameOnly && _recorder.Current is MatchRecord cur)
        {
            var k = tip.Kind + ":" + outcome.ToString().ToLowerInvariant();
            cur.TipOutcomes[k] = cur.TipOutcomes.TryGetValue(k, out var n) ? n + 1 : 1;
        }
        // журнал подсказок: всё, что помощник хотел сказать, и что с этим стало — привязано к матчу и времени игры
        var g = LastGsi;
        string? matchId = g is { IsInProgress: true } ? g.MatchId : null;
        TipLog.Add(matchId, matchId != null ? g!.ClockTime : null, tip, outcome, DateTime.Now);
        if (outcome != TipOutcome.Shown) Log.Info($"Совет «{tip.Kind}»: {outcome switch {
            TipOutcome.Dropped => "не показан (оверлей скрыт или уведомления выключены)",
            TipOutcome.Expired => "истёк, не дождавшись показа",
            TipOutcome.Invalidated => "снят — уже неактуален",
            TipOutcome.Merged => "заменён более свежим",
            TipOutcome.Deferred => "сохранён для разбора после матча",
            _ => outcome.ToString() }}");
    }

    /// <summary>Горячая клавиша «Рошан убит»: отметить; повторное нажатие в течение 10 секунд снимает отметку.</summary>
    public void ToggleRoshanMark()
    {
        var L = Loc.Instance;
        var clock = GameClock;
        if (clock == null)
        {
            ShowEventToast(L.Get("rosh_header").ToUpperInvariant(), L.Get("rosh_need_clock"), warning: true, priority: TipPriority.Urgent, kind: "rosh_mark", ttlSec: 8);
            return;
        }
        if (Roshan.KilledAt is int k && clock.Value - k < 10)
        {
            Roshan.Clear();
            ShowEventToast(L.Get("rosh_header").ToUpperInvariant(), L.Get("rosh_cleared"), priority: TipPriority.Urgent, kind: "rosh_mark", ttlSec: 8);
        }
        else
        {
            Roshan.MarkKilled(clock.Value);
            ShowEventToast(L.Get("rosh_header").ToUpperInvariant(), L.Format("rosh_marked",
                GameTimers.Clock(clock.Value + RoshanTracker.RespawnMinSec), GameTimers.Clock(clock.Value + RoshanTracker.RespawnMaxSec)),
                priority: TipPriority.Urgent, kind: "rosh_mark", ttlSec: 8);
        }
        RefreshOverlay();
        RaiseChanged();
    }

    public void ClearRoshanMark()
    {
        Roshan.Clear();
        RefreshOverlay();
        RaiseChanged();
    }

    /// <summary>Строка таймеров для оверлея: два ближайших события и Рошан.</summary>
    /// <summary>Строка таймеров для обзора и оверлея.</summary>
    public string TimersLineText => BuildTimersLine();

    /// <summary>До одного из показанных событий или возможного появления Рошана меньше 10 секунд.</summary>
    private bool TimersUrgentNow()
    {
        if (!Settings.Current.Timers.Enabled || GameClock is not int clock) return false;
        if (GameTimers.Upcoming(Database.Timers, clock, TimerOn, 8).Any(e => e.InSec <= 10 && (e.Rule.IntervalSec > 60 || e.Rule.IntervalSec == 0 || e.InSec <= 15)))
            return true;
        var r = Roshan.State(clock);
        return r.Phase == RoshanPhase.Dead && r.RespawnMinAt - clock is >= 0 and <= 10;
    }

    /// <summary>Доля золота до покупки предмета (0…1) или -1, если цена или золото неизвестны.</summary>
    public double GoldProgress(string itemKey)
    {
        var cost = Costs.Cost(itemKey);
        var g = LastGsi;
        if (cost is not int c || c <= 0 || g?.Gold is not int gold || !g.IsInProgress || DateTime.UtcNow - g.ReceivedUtc > TimeSpan.FromSeconds(10)) return -1;
        return Math.Clamp((double)gold / c, 0, 1);
    }

    private string BuildTimersLine()
    {
        if (!Settings.Current.Timers.Enabled || GameClock is not int clock) return "";
        var L = Loc.Instance;
        // частые события (стаки раз в минуту) показываем только в последние 15 секунд, чтобы не занимали строку
        var parts = GameTimers.Upcoming(Database.Timers, clock, TimerOn, 8)
            .Where(e => e.Rule.IntervalSec > 60 || e.Rule.IntervalSec == 0 || e.InSec <= 15)
            .Take(2)
            .Select(e => $"{TimerName(e.Rule)} {GameTimers.Clock(e.InSec)}").ToList();
        var r = Roshan.State(clock);
        if (r.Phase == RoshanPhase.Dead)
            parts.Add(r.AegisLeftSec > 0
                ? $"{L.Get("rosh_aegis_short")} {GameTimers.Clock(r.AegisLeftSec)}"
                : $"{L.Get("rosh_short")} {GameTimers.Clock(r.RespawnMinAt - clock)}–{GameTimers.Clock(r.RespawnMaxAt - clock)}");
        else if (r.Phase == RoshanPhase.MaybeAlive)
            parts.Add($"{L.Get("rosh_short")} ≤{GameTimers.Clock(r.RespawnMaxAt - clock)}");
        return string.Join(" · ", parts).ToUpperInvariant();
    }

    /// <summary>«Нужно ещё 650 золота» / «Хватает на покупку» — только когда есть цена и золото из GSI.</summary>
    public string? GoldLine(string itemKey)
    {
        var cost = Costs.Cost(itemKey);
        var g = LastGsi;
        if (cost == null || g?.Gold is not int gold || !g.IsInProgress || DateTime.UtcNow - g.ReceivedUtc > TimeSpan.FromSeconds(10)) return null;
        return gold >= cost.Value ? Loc.T("ov_gold_ready") : Loc.Instance.Format("ov_gold_need", cost.Value - gold);
    }

    public string? NoteFor(Hero? hero) =>
        hero != null && Settings.Current.HeroNotes.TryGetValue(hero.Key, out var n) && !string.IsNullOrWhiteSpace(n) ? n : null;

    private static string Short(string text, int max)
    {
        var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + "…";
    }

    /// <summary>Однократно показать личную заметку о только что распознанном герое противника.</summary>
    public void AnnounceEnemyNotes(IEnumerable<Hero> enemies)
    {
        foreach (var hero in enemies)
        {
            if (NoteFor(hero) is not string note || !_notedEnemies.Add(hero.Id)) continue;
            ShowEventToast((Loc.T("notes_toast") + " · " + hero.Name).ToUpperInvariant(), Short(note, 120), kind: "notes", key: "notes:" + hero.Key, ttlSec: 90);
        }
    }

    private void ApplyPool()
    {
        var s = Settings.Current;
        Analyzer.PoolMode = s.PoolMode;
        Analyzer.Pool = s.FavoriteHeroKeys.Select(Database.FindHero).Where(h => h != null).Select(h => h!.Id).ToHashSet();
        Analyzer.Personal = s.Data.UsePersonalStats && s.Data.SteamAccountId != null
            ? PlayerHeroes.All.ToDictionary(kv => kv.Key, kv => (kv.Value.Games, kv.Value.Wins))
            : new Dictionary<int, (int, int)>();
        Analyzer.PersonalAgainst = s.Data.UsePersonalStats && s.Data.SteamAccountId != null
            ? PlayerHeroes.All.Where(kv => kv.Value.AgainstGames > 0).ToDictionary(kv => kv.Key, kv => (kv.Value.AgainstGames, kv.Value.AgainstWins))
            : new Dictionary<int, (int, int)>();
    }

    private MatchTick ToTick(GameStateSnapshot g)
    {
        Hero? hero = g.HeroId is > 0 ? Database.FindHero(g.HeroId.Value) : Database.FindHero(g.HeroName);
        return new MatchTick(g.MatchId, g.GameState, g.ClockTime, g.TeamName, g.WinTeam, hero?.Id ?? g.HeroId, hero?.Key,
            g.HeroLevel, g.Kills, g.Deaths, g.Assists, g.LastHits, g.Denies, g.Gpm, g.Xpm, g.Gold, g.ItemSlots,
            CurrentPosition, g.ReceivedUtc, g.GoldUnreliable, g.BuybackCost, g.BuybackCooldown, g.HeroX, g.HeroY);
    }

    private void SaveMatch(MatchRecord record, bool attachAlerts = true)
    {
        if (attachAlerts && (record.MatchId == _lastMatchId || _lastMatchId == null))
        {
            record.Alerts = new Dictionary<string, int>(Alerts.Counts);
            record.AlertsIgnored = new Dictionary<string, int>(Alerts.Ignored);
            record.AlertsFollowed = new Dictionary<string, int>(Alerts.Followed);
        }
        CommitMatch(record);
    }

    /// <summary>
    /// Записать матч на диск. Только после успешной записи матч считается сохранённым: итог, импорт, цели.
    /// Неудача — матч ждёт повторной попытки (раз в 30 секунд), пользователь видит одно предупреждение.
    /// </summary>
    private bool CommitMatch(MatchRecord record)
    {
        if (!History.Save(record))
        {
            lock (_unsaved)
            {
                _unsaved.RemoveAll(x => x.Id == record.Id);
                _unsaved.Add(record);
                History.SavePending(_unsaved.ToList());   // очередь переживёт закрытие программы
            }
            _unsavedRetryUtc = DateTime.UtcNow + UnsavedRetryEvery;
            Log.Warn($"История: матч {record.MatchId} не записан на диск — повторю попытку");
            if (!_saveFailWarned)
            {
                _saveFailWarned = true;
                ShowEventToast(Loc.T("hist_save_fail_header").ToUpperInvariant(), Loc.T("hist_save_fail"), warning: true,
                    priority: TipPriority.Background, kind: "history_save", ttlSec: 900);
            }
            RaiseChanged();
            return false;
        }
        lock (_unsaved)
        {
            if (_unsaved.RemoveAll(x => x.Id == record.Id) > 0) History.SavePending(_unsaved.ToList());
            if (_unsaved.Count == 0) _saveFailWarned = false;
        }
        History.ClearCheckpoint(record.Id);
        _lastSaved = record;
        MatchSavedSinceGameStart = true;
        Log.Info($"История: сохранён матч {record.MatchId} ({record.Result}){(record.Partial ? " — неполная запись" : "")}");
        BuildPostMatch(record);
        _ = AutoImportAfterMatchAsync(record.MatchId);
        try
        {
            var outcome = Goals.OnMatchSaved(record, History.All());
            if (outcome is { } o)
            {
                ShowEventToast(Loc.T("goal_header").ToUpperInvariant(),
                    Loc.Instance.Format(o.Result.Achieved ? "goal_done_toast" : "goal_failed_toast", GoalText(o.Goal)),
                    priority: TipPriority.Background, kind: "goal_result", ttlSec: 900);
            }
        }
        catch (Exception ex) { Log.Error("Цели: ошибка проверки", ex); }
        return true;
    }

    /// <summary>Матчей, которые пока не удалось записать на диск.</summary>
    public int UnsavedMatches { get { lock (_unsaved) return _unsaved.Count; } }

    private void RetryUnsavedMatches()
    {
        List<MatchRecord> pending;
        lock (_unsaved)
        {
            if (_unsaved.Count == 0 || DateTime.UtcNow < _unsavedRetryUtc) return;
            pending = _unsaved.ToList();
        }
        var needed = UnsavedQueuePolicy.StillNeeded(pending, History.Find);
        lock (_unsaved)
            if (_unsaved.RemoveAll(x => needed.All(n => n.Id != x.Id)) > 0) History.SavePending(_unsaved.ToList());
        foreach (var r in needed) CommitMatch(r);
    }

    /// <summary>Матчи, которые не удалось записать в прошлый раз, — снова в очередь, первая попытка сразу.</summary>
    private void LoadUnsavedQueue()
    {
        var pending = History.LoadPending();
        if (pending.Count == 0) return;
        // авария между записью матча и очисткой очереди: в истории уже есть не менее полная версия — её не трогаем
        var keep = UnsavedQueuePolicy.StillNeeded(pending, History.Find);
        lock (_unsaved)
        {
            foreach (var r in keep)
                if (_unsaved.All(x => x.Id != r.Id)) _unsaved.Add(r);
            if (keep.Count != pending.Count) History.SavePending(_unsaved.ToList());
        }
        _unsavedRetryUtc = DateTime.MinValue;
        if (keep.Count > 0) Log.Info($"История: несохранённых матчей с прошлого запуска: {keep.Count} — повторяю запись");
        if (keep.Count != pending.Count) Log.Info($"История: из очереди убрано {pending.Count - keep.Count} — в истории уже есть более полные версии");
    }

    // ------------------------------------------------------------------ контрольная запись незаконченного матча

    private void LoadPendingCheckpoint()
    {
        var cp = History.LoadCheckpoint();
        if (cp == null) return;
        if (DateTime.UtcNow - cp.Value.SavedUtc > CheckpointMaxAge) ApplyCheckpoint(cp.Value.Record, null, "контрольная запись старше 3 часов");
        else
        {
            _pendingCheckpoint = cp;
            Log.Info($"История: найден незаконченный матч {cp.Value.Record.MatchId} — продолжу, если он ещё идёт");
        }
    }

    /// <summary>Первый снимок с номером матча решает судьбу контрольной записи (см. <see cref="CheckpointPolicy"/>).</summary>
    private void ResolvePendingCheckpoint(string? matchId)
    {
        if (_pendingCheckpoint is not { } pend || string.IsNullOrWhiteSpace(matchId) || matchId == "0") return;
        _pendingCheckpoint = null;
        ApplyCheckpoint(pend.Record, matchId, matchId == pend.Record.MatchId ? "тот же матч" : "начался другой матч");
    }

    private void ApplyCheckpoint(MatchRecord cp, string? liveMatchId, string why)
    {
        var saved = History.Find(cp.Id);
        switch (CheckpointPolicy.Decide(cp, saved, liveMatchId, _recorder.Current != null))
        {
            case CheckpointAction.Resume:
                _recorder.Restore(cp);
                Log.Info($"История: матч {cp.MatchId} продолжен после перезапуска (пропуск отмечен в покрытии)");
                break;
            case CheckpointAction.Discard:
                History.ClearCheckpoint(cp.Id);
                Log.Info($"История: контрольная запись матча {cp.MatchId} устарела — в истории уже есть более полная");
                break;
            default:
                cp.Ended ??= false;
                Log.Info($"История: незаконченный матч {cp.MatchId} сохраняется как неполный ({why})");
                if (cp.DurationSec >= 120) SaveMatch(cp, attachAlerts: false);
                else History.ClearCheckpoint(cp.Id);
                break;
        }
    }

    /// <summary>Итог последнего матча, который ещё не показан (окно откроется, когда пользователь уйдёт из игры).</summary>
    public PostMatchSummary? PendingPostMatch { get; private set; }

    /// <summary>Готов итог матча (сразу после игры).</summary>
    public event EventHandler<PostMatchSummary>? PostMatchReady;

    /// <summary>Забрать итог для показа (повторно не покажется).</summary>
    public PostMatchSummary? TakePostMatch()
    {
        var s = PendingPostMatch;
        PendingPostMatch = null;
        return s;
    }

    private void BuildPostMatch(MatchRecord record)
    {
        if (!Settings.Current.Automation.PostMatchSummary || !PostMatch.Worth(record)) return;
        try
        {
            var summary = PostMatch.Build(record, History.All(), key => Database.FindItem(key)?.Name);
            if (!summary.HasContent) return;
            PendingPostMatch = summary;
            var L = Loc.Instance;
            var line = summary.MainMistake ?? summary.Good.FirstOrDefault();
            if (line != null) ShowEventToast(L.Get("pm_header").ToUpperInvariant(), line.Text(L.Language) + " " + L.Get("pm_toast_more"),
                priority: TipPriority.Background, kind: "post_match", ttlSec: 900);
            PostMatchReady?.Invoke(this, summary);
        }
        catch (Exception ex) { Log.Error("Итог матча", ex); }
    }

    // ------------------------------------------------------------------ личный темп и цели

    /// <summary>Начался новый матч: подготовить сравнение с обычным темпом и напомнить цель.</summary>
    private void OnMatchStarted(MatchRecord current)
    {
        _paceMatchId = current.Id;
        _paceItemsSeen.Clear();
        var history = History.All();
        _pace = new PaceAdvisor(history, current.HeroId, current.Position ?? CurrentPosition, current.Id);
        Alerts.CooldownMultipliers.Clear();
        foreach (var (k, v) in MatchAlerts.AdaptiveMultipliers(history)) Alerts.CooldownMultipliers[k] = v;
        if (Goals.Active is CoachGoal goal)
        {
            ShowEventToast(Loc.T("goal_header").ToUpperInvariant(), GoalText(goal), kind: "goal_start", ttlSec: 120);
        }
    }

    private void TickPace(int clock)
    {
        if (!Settings.Current.Alerts.Pace || _pace == null || _recorder.Current is not MatchRecord cur) return;
        // роль уточнилась после старта (автоопределение) — «обычный темп» берём для настоящей роли, а не прошлой игры
        if (cur.Position is Position role && role != _pace.Position) _pace = _pace.ForPosition(role);
        var L = Loc.Instance;
        if (_pace.CheckpointDue(clock, LastGsi?.LastHits) is PaceCheck check)
        {
            var key = check.Delta >= 0 ? "pace_lh_ahead" : "pace_lh_behind";
            // основание вывода — словами: «по 5 вашим играм на этом герое»
            var basis = L.Format(check.SameHero ? "pace_basis_hero" : "pace_basis_role", check.Matches);
            ShowEventToast(L.Get("pace_header").ToUpperInvariant(),
                L.Format(key, GameTimers.Clock(check.Clock), check.Current, check.Usual, Math.Abs(check.Delta)) + " · " + basis, warning: check.Delta < 0,
                kind: "pace_lh", key: "pace_lh:" + check.Clock, ttlSec: 60, inGame: true);
        }
        foreach (var (item, at) in cur.ItemTimings.ToList())
        {
            if (!_paceItemsSeen.Add(item) || !Settings.Current.Automation.ShowBuild) continue;
            if (at <= 0 || clock - at > 30 || cur.IsApproxTiming(item)) continue; // только только что купленные и точно
            var cost = Costs.Cost(item);
            if (cost is < 1500) continue;
            if (_pace.ItemDue(cur, item, at) is not ItemPace pace || Math.Abs(pace.Delta) < 60) continue;
            var name = Database.FindItem(item)?.DisplayName(L.Language) ?? item;
            ShowEventToast(L.Get("pace_header").ToUpperInvariant(), L.Format(pace.Delta > 0 ? "pace_item_late" : "pace_item_early",
                name, GameTimers.Clock(at), GameTimers.Clock(Math.Abs(pace.Delta)), GameTimers.Clock(pace.Bench.AvgSec))
                + " · " + L.Format(pace.Bench.SameHero ? "pace_basis_hero" : "pace_basis_role", pace.Bench.Matches), warning: pace.Delta > 0,
                kind: "pace_item", key: "pace_item:" + item, ttlSec: 45, inGame: true);
        }
    }

    /// <summary>Текст цели для интерфейса и уведомлений.</summary>
    public string GoalText(CoachGoal goal)
    {
        var L = Loc.Instance;
        return goal.Kind switch
        {
            GoalKind.GoldOnDeath => L.Format("goal_gold_death", goal.Target),
            GoalKind.FewerDeaths => L.Format("goal_fewer_deaths", goal.Target),
            GoalKind.LastHits10 => L.Format("goal_lh10", goal.Target),
            GoalKind.CarryTeleport => L.Get("goal_tp"),
            GoalKind.CourierStash => L.Get("goal_stash"),
            GoalKind.ItemTiming => L.Format("goal_item", Database.FindItem(goal.ItemKey ?? "")?.DisplayName(L.Language) ?? goal.ItemKey ?? "?",
                GameTimers.Clock(goal.Target)),
            _ => goal.Kind.ToString()
        };
    }

    // ------------------------------------------------------------------ сводка драфта

    /// <summary>Видимые герои противника для сводки: автоматический драфт или ручной.</summary>
    public IReadOnlyList<Hero> BriefingEnemies => AutoDraftLive && AutoEnemies.Count > 0 ? AutoEnemies : Draft.Enemies;

    /// <summary>Сводка по драфту противника (от трёх героев).</summary>
    public Briefing? CurrentBriefing =>
        BriefingEnemies.Count >= 3 ? BriefingBuilder.Build(BriefingEnemies, CurrentPosition) : null;

    /// <summary>Предмет, который стоит взять первым против этого драфта (только в режиме тренировки — как и остальные советы по врагам).</summary>
    public ItemPick? BriefingItem
    {
        get
        {
            if (CurrentHero == null || BriefingEnemies.Count < 3) return null;
            if (!ComplianceGate.IsAllowed(Feature.EnemyAwareItemAdvice, Settings.Current.UsageContext)) return null;
            return Builds.Build(CurrentHero, CurrentPosition, BriefingEnemies)[BuildPhase.Situational].FirstOrDefault();
        }
    }

    /// <summary>Строки сводки для оверлея и окна.</summary>
    public IReadOnlyList<(string Title, string Text)> BriefingRows(int maxLines = 4)
    {
        var L = Loc.Instance;
        var b = CurrentBriefing;
        var rows = new List<(string, string)>();
        if (b == null) return rows;
        if (CurrentTiming?.Plan is { } plan)
            rows.Add((L.Get("spike_plan"), L.Language == Language.Russian ? plan.Ru : plan.En));
        if (b.LaneOpponents.Count > 0)
            rows.Add((L.Get("brief_lane"), string.Join(", ", b.LaneOpponents.Select(h => h.DisplayName(L.Language)))));
        if (Settings.Current.Automation.LaneTips)
            foreach (var a in LaneAdvice.Take(2))
                if (a.Tips.FirstOrDefault() is { } tip)
                    rows.Add((a.Hero.DisplayName(L.Language), L.Language == Language.Russian ? tip.Ru : tip.En));
        if (BriefingItem is ItemPick item)
            rows.Add((L.Get("brief_item"), item.Item.DisplayName(L.Language)));
        foreach (var line in b.Lines.Take(maxLines))
            rows.Add((string.Join(", ", line.Heroes.Select(h => h.DisplayName(L.Language))), L.Language == Language.Russian ? line.Ru : line.En));
        return rows;
    }

    /// <summary>Сводку показываем в оверлее между драфтом и началом игры.</summary>
    private bool BriefingPhase =>
        LastGsi != null && DateTime.UtcNow - LastGsi.ReceivedUtc < TimeSpan.FromSeconds(20)
        && (LastGsi.GameState is "DOTA_GAMERULES_STATE_STRATEGY_TIME" or "DOTA_GAMERULES_STATE_TEAM_SHOWCASE" or "DOTA_GAMERULES_STATE_WAIT_FOR_MAP_TO_LOAD"
            || LastGsi.GameState == "DOTA_GAMERULES_STATE_PRE_GAME")
        && CurrentBriefing != null;

    // ------------------------------------------------------------------ сеть: патч, цены, обновления

    /// <summary>Новый патч, о котором стоит сообщить: новее встроенной базы и ещё не отмечен пользователем.</summary>
    public string? NewPatchNotice
    {
        get
        {
            var latest = Patch.Latest;
            if (latest == null || !PatchVersion.IsNewer(latest, Database.PatchBaseline)) return null;
            return PatchVersion.Short(latest) == PatchVersion.Short(Settings.Current.Data.AcknowledgedPatch) ? null : latest;
        }
    }

    public void SetAvailableUpdate(ReleaseInfo? info)
    {
        AvailableUpdate = info;
        RaiseChanged();
    }

    /// <summary>
    /// Проверить обновление через общий координатор. Удачный ответ обновляет <see cref="AvailableUpdate"/>,
    /// неудачный — нет (найденная раньше версия не пропадает из-за сбоя сети).
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(bool manual)
    {
        var repo = Settings.Current.Data.UpdateRepo;
        var result = await Updates.CheckAsync(repo ?? "", manual);
        if (!result.FromCache && !result.Succeeded) Log.Warn($"Обновление ROSHIQ: {result.Status} {result.Message}");
        if (result.Succeeded) AvailableUpdate = result.Release;
        RaiseChanged();
        return result;
    }

    public void AcknowledgePatch()
    {
        var latest = Patch.Latest;
        if (latest != null) Settings.Update(s => s.Data.AcknowledgedPatch = latest);
        RaiseChanged();
    }

    /// <summary>
    /// Фоновая проверка (только если сеть разрешена): цены предметов раз в 3 дня, патч и версия ROSHIQ раз в сутки.
    /// force — по кнопке, без учёта интервалов. Возвращает текст ошибки или null.
    /// </summary>
    public async Task<string?> RunOnlineChecksAsync(bool force)
    {
        var d = Settings.Current.Data;
        if (!d.AllowOnlineStats || (!force && !d.AutoCheckOnline) || _onlineCheckRunning) return null;
        _onlineCheckRunning = true;
        string? error = null;
        try
        {
            using var client = new OpenDotaClient(d.OpenDotaApiKey);
            var now = DateTime.UtcNow;
            if (force || Costs.UpdatedUtc == null || now - Costs.UpdatedUtc.Value > TimeSpan.FromDays(3))
            {
                try
                {
                    var costs = await client.GetItemCostsAsync(CancellationToken.None);
                    if (costs.Count > 0) Costs.Replace(costs, now);
                }
                catch (Exception ex) { error = ex.Message; Log.Warn("Цены предметов: " + ex.Message); }
            }
            if (force || Patch.CheckedUtc == null || now - Patch.CheckedUtc.Value > TimeSpan.FromDays(1))
            {
                try
                {
                    var (name, date) = await client.GetLatestPatchAsync(CancellationToken.None);
                    if (name != null) Patch.Set(name, date, now);
                }
                catch (Exception ex) { error ??= ex.Message; Log.Warn("Патч: " + ex.Message); }
            }
            if (force || PatchNotes == null || now - PatchNotes.FetchedUtc > TimeSpan.FromDays(1)
                || (Patch.Latest != null && PatchVersion.Short(Patch.Latest) != PatchVersion.Short(PatchNotes.Patch)))
            {
                try
                {
                    var notes = await client.GetPatchNotesAsync(CancellationToken.None);
                    if (notes != null) { PatchNotes = notes; notes.Save(PatchNotesPath); }
                }
                catch (Exception ex) { error ??= ex.Message; Log.Warn("Изменения патча: " + ex.Message); }
            }
            if (!string.IsNullOrWhiteSpace(d.UpdateRepo))
            {
                try
                {
                    var r = await CheckForUpdateAsync(manual: false);
                    if (!r.Succeeded) error ??= AppUpdater.Describe(r);
                }
                catch (Exception ex) { error ??= ex.Message; Log.Warn("Обновление ROSHIQ: " + ex.Message); }
            }

            // обновление файлов данных с GitHub и проверка их актуальности — раз в сутки
            if (force || d.DataCheckedUtc == null || now - d.DataCheckedUtc.Value > TimeSpan.FromDays(1))
            {
                if (d.AutoUpdateData && !string.IsNullOrWhiteSpace(d.UpdateRepo))
                {
                    try
                    {
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("Roshiq/" + AppUpdater.CurrentVersion);
                        var result = await RemoteData.UpdateAsync(http, d.UpdateRepo!, Database.DataVersion, AppUpdater.CurrentVersion,
                            AppPaths.DataRemote, CancellationToken.None);
                        DataUpdateMessage = result.Message;
                        if (result.Updated)
                            await _dispatcher.InvokeAsync(() =>
                            {
                                ReloadDatabase();
                                ShowEventToast(Loc.T("data_updated_header").ToUpperInvariant(), Loc.Instance.Format("data_updated", result.Version ?? ""),
                                    priority: TipPriority.Background, kind: "data_updated", ttlSec: 1800);
                            });
                    }
                    catch (Exception ex) { error ??= ex.Message; DataUpdateMessage = ex.Message; Log.Warn("Обновление данных: " + ex.Message); }
                }
                try
                {
                    var heroes = await client.GetHeroListAsync(CancellationToken.None);
                    var items = await client.GetItemIdMapAsync(CancellationToken.None);
                    _itemIdMap ??= items;
                    Audit = DataAudit.Build(Database, Patch.Latest, heroes, items.Values.ToList());
                    Log.Info($"Проверка данных: новых героев {Audit.NewHeroes.Count}, исчезнувших предметов {Audit.RemovedItems.Count}, патч {(Audit.PatchBehind ? "новее базы" : "совпадает")}");
                }
                catch (Exception ex) { error ??= ex.Message; Log.Warn("Проверка данных: " + ex.Message); }
                var checkedAt = now;
                await _dispatcher.InvokeAsync(() => Settings.Update(x => x.Data.DataCheckedUtc = checkedAt));
            }
        }
        finally
        {
            _onlineCheckRunning = false;
            _ = _dispatcher.InvokeAsync(() => { RefreshOverlay(); RaiseChanged(); });
        }
        return error;
    }

    /// <summary>Итог последней проверки актуальности данных (null — ещё не проверялось в этом запуске).</summary>
    public DataAudit? Audit { get; private set; }
    /// <summary>Что произошло при последней проверке обновления данных.</summary>
    public string? DataUpdateMessage { get; private set; }

    private readonly CancellationTokenSource _backgroundCts = new();
    private bool _matchupRefreshRunning;

    /// <summary>Статистика контрпиков старше недели — тихо обновить в фоне (≈1 запрос в секунду, ~2 минуты).</summary>
    private async Task AutoRefreshMatchupsAsync()
    {
        var d = Settings.Current.Data;
        if (!d.AllowOnlineStats || !d.AutoRefreshMatchups || _matchupRefreshRunning) return;
        if (Stats.LastUpdatedUtc is DateTime last && DateTime.UtcNow - last < TimeSpan.FromDays(7)) return;
        _matchupRefreshRunning = true;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), _backgroundCts.Token); // не мешать запуску программы
            using var client = new OpenDotaClient(d.OpenDotaApiKey);
            int ok = await client.RefreshAsync(Stats, Database.Heroes.Select(h => h.Id).ToList(), null, _backgroundCts.Token);
            Log.Info($"Статистика контрпиков обновлена в фоне: {ok}/{Database.Heroes.Count}");
            await _dispatcher.InvokeAsync(() => { RebuildEngines(); RaiseChanged(); });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("Фоновое обновление статистики: " + ex.Message); }
        finally { _matchupRefreshRunning = false; }
    }

    /// <summary>Вернуться к встроенным данным (удалить обновление с GitHub).</summary>
    public void ResetRemoteData()
    {
        RemoteData.Reset(AppPaths.DataRemote);
        Settings.Update(x => x.Data.DataCheckedUtc = null);
        ReloadDatabase();
    }

    public static BuildPhase PhaseForClock(int? clock) => clock switch
    {
        null or < 0 => BuildPhase.Start,
        < 600 => BuildPhase.Early,
        < 1800 => BuildPhase.Mid,
        _ => BuildPhase.Late
    };

    private void RaiseChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("Ошибка обновления интерфейса", ex); }
    }

    public void Dispose()
    {
        _tick.Stop();
        try { _backgroundCts.Cancel(); } catch { }
        // незаконченный матч не закрываем: контрольная запись — после перезапуска он продолжится или сохранится как неполный
        try { if (_recorder.Current is MatchRecord live) History.SaveCheckpoint(live); } catch { }
        try
        {
            lock (_unsaved)
            {
                // то же правило, что при повторных попытках: устаревшую версию поверх более полной не пишем
                var needed = UnsavedQueuePolicy.StillNeeded(_unsaved, History.Find);
                _unsaved.RemoveAll(r => needed.All(n => n.Id != r.Id) || History.Save(r));
                History.SavePending(_unsaved.ToList());   // что не записалось — останется в очереди до следующего запуска
            }
        }
        catch { }
        try { Settings.Save(); } catch { }
        Gsi?.Dispose();
        Overlay.Dispose();
    }
}
