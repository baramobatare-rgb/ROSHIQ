using System.Text.Json;
using System.Text.Json.Serialization;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Точка графика матча (раз в 30 секунд игрового времени).</summary>
public sealed class MatchSample
{
    public int Clock { get; set; }
    public int? Gold { get; set; }
    public int? Gpm { get; set; }
    public int? Xpm { get; set; }
    public int? LastHits { get; set; }
    public int? Level { get; set; }
}

/// <summary>Смерть своего героя: когда и в каком состоянии (по снимку GSI прямо перед смертью).</summary>
public sealed class DeathEvent
{
    public int Clock { get; set; }
    /// <summary>Ненадёжное золото при себе (или всё золото, если GSI не разделяет).</summary>
    public int GoldAtRisk { get; set; }
    public bool HadTeleport { get; set; }
    /// <summary>Байбэк был доступен (перезарядка 0 и хватало золота). null — неизвестно.</summary>
    public bool? BuybackReady { get; set; }
    public int Level { get; set; }
    /// <summary>Где герой был перед смертью (мировые координаты GSI), если известно.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
}

/// <summary>Один сыгранный матч в локальной истории (записан по GSI или импортирован из OpenDota).</summary>
public sealed class MatchRecord
{
    public string Id { get; set; } = "";
    public string? MatchId { get; set; }
    public string Source { get; set; } = "GSI";
    public DateTime StartedUtc { get; set; }
    public int HeroId { get; set; }
    public string? HeroKey { get; set; }
    public Position? Position { get; set; }
    public string? Team { get; set; }
    public MatchResult Result { get; set; }
    public int DurationSec { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int LastHits { get; set; }
    public int Denies { get; set; }
    public int Gpm { get; set; }
    public int Xpm { get; set; }
    public int Level { get; set; }
    public List<MatchSample> Samples { get; set; } = new();
    /// <summary>Игровое время (сек), когда предмет впервые появился в инвентаре или тайнике.</summary>
    public Dictionary<string, int> ItemTimings { get; set; } = new();
    /// <summary>Сколько раз сработали ситуативные подсказки (ключи MatchAlerts).</summary>
    public Dictionary<string, int> Alerts { get; set; } = new();
    /// <summary>Сколько раз подсказку проигнорировали (условие держалось ещё минуту после показа).</summary>
    public Dictionary<string, int> AlertsIgnored { get; set; } = new();
    /// <summary>Сколько раз подсказку выполнили (условие ушло в течение минуты после показа).</summary>
    public Dictionary<string, int> AlertsFollowed { get; set; } = new();
    /// <summary>Исходы советов диспетчера: «вид:исход» → сколько раз (показан, истёк, неактуален…). Для проверки пользы.</summary>
    public Dictionary<string, int> TipOutcomes { get; set; } = new();
    public List<DeathEvent> DeathEvents { get; set; } = new();

    /// <summary>
    /// 0.17.5: отрезки игрового времени, по которым реально приходили данные GSI. Пусто — старая запись:
    /// тогда покрытие оценивается по точкам графика (<see cref="Samples"/>).
    /// </summary>
    public List<CoverageSpan> Coverage { get; set; } = new();
    /// <summary>
    /// Предметы, впервые увиденные сразу после пропуска данных (или когда запись началась не с начала игры):
    /// их время в <see cref="ItemTimings"/> — верхняя граница, а не момент покупки. В сравнениях не участвуют.
    /// </summary>
    public List<string> ApproxItems { get; set; } = new();

    public bool IsApproxTiming(string itemKey) => ApproxItems.Contains(itemKey, StringComparer.OrdinalIgnoreCase);

    /// <summary>Только точные тайминги предметов (без увиденных после пропуска).</summary>
    [JsonIgnore] public IEnumerable<KeyValuePair<string, int>> ExactItemTimings => ItemTimings.Where(kv => !IsApproxTiming(kv.Key));

    /// <summary>Смерти, которые известны только по счётчику (пропуск данных): ни места, ни времени — на карту и в фазы не идут.</summary>
    public int UnlocatedDeaths { get; set; }
    /// <summary>Видели конец матча (POST_GAME). null — запись старше 0.17.5, неизвестно.</summary>
    public bool? Ended { get; set; }
    /// <summary>Запись продолжена после перезапуска программы (из контрольной записи).</summary>
    public bool Resumed { get; set; }

    /// <summary>Доля отрезка [from, to) игрового времени, покрытая данными (0…1). Конец ограничен длительностью матча.</summary>
    public double CoverageOf(int from, int to)
    {
        to = Math.Min(to, DurationSec);
        if (to <= from) return 1;
        var spans = Coverage.Count > 0 ? Coverage : SpansFromSamples();
        long covered = 0;
        foreach (var sp in spans)
        {
            int a = Math.Max(from, sp.From), b = Math.Min(to, sp.To);
            if (b > a) covered += b - a;
        }
        return Math.Min(1.0, (double)covered / (to - from));
    }

    /// <summary>Фаза записана достаточно полно (≥ 85%), чтобы делать по ней выводы «чего не было».</summary>
    public bool Covers(int from, int to) => !FromGsi || CoverageOf(from, to) >= 0.85;

    /// <summary>Запись неполная: пропуски данных, смерти без места или не видели конец матча.</summary>
    [JsonIgnore] public bool Partial => FromGsi && (UnlocatedDeaths > 0 || Ended == false || CoverageOf(0, DurationSec) < 0.85);

    /// <summary>Для старых записей: отрезки между точками графика (раз в 30 с), разрыв больше 45 с — пропуск.</summary>
    private List<CoverageSpan> SpansFromSamples()
    {
        var list = new List<CoverageSpan>();
        foreach (var s in Samples.OrderBy(x => x.Clock))
        {
            if (list.Count > 0 && s.Clock - list[^1].To <= 45) list[^1].To = s.Clock;
            else list.Add(new CoverageSpan { From = s.Clock, To = s.Clock });
        }
        if (list.Count > 0)
        {
            if (list[0].From <= 45) list[0].From = 0;                       // первая точка — в начале игры
            if (DurationSec - list[^1].To <= 45) list[^1].To = DurationSec; // последняя — в конце
        }
        return list;
    }

    /// <summary>Добивания к моменту clock (по ближайшей точке графика в пределах 40 секунд).</summary>
    public int? LastHitsAt(int clock) => SampleAt(clock)?.LastHits;
    public int? GpmAt(int clock) => SampleAt(clock)?.Gpm;

    private MatchSample? SampleAt(int clock)
    {
        MatchSample? best = null;
        foreach (var s in Samples)
            if (Math.Abs(s.Clock - clock) <= 40 && (best == null || Math.Abs(s.Clock - clock) < Math.Abs(best.Clock - clock)))
                best = s;
        return best;
    }

    [JsonIgnore] public bool FromGsi => Source == "GSI";
}

/// <summary>Что делать с контрольной записью незаконченного матча.</summary>
public enum CheckpointAction
{
    /// <summary>Тот же матч ещё идёт — продолжить запись.</summary>
    Resume,
    /// <summary>Матч уже не продолжить — сохранить как неполный.</summary>
    SaveAsPartial,
    /// <summary>В истории уже есть завершённая или не менее полная запись — контрольную выбросить.</summary>
    Discard
}

public static class CheckpointPolicy
{
    /// <summary>
    /// Запись в истории не хуже кандидата того же матча (кандидата сохранять не нужно): завершённая главнее
    /// незавершённой, иначе — та, что длиннее (при равной длине остаётся сохранённая).
    /// </summary>
    public static bool IsAtLeastAsComplete(MatchRecord existing, MatchRecord candidate)
    {
        if (existing.Ended == true && candidate.Ended != true) return true;
        if (candidate.Ended == true && existing.Ended != true) return false;
        return existing.DurationSec >= candidate.DurationSec;
    }

    /// <param name="checkpoint">Контрольная запись.</param>
    /// <param name="saved">Запись того же Id, уже лежащая в истории (если есть).</param>
    /// <param name="liveMatchId">Номер матча из первого свежего снимка GSI (null — пока неизвестно / решение при запуске).</param>
    /// <param name="recorderBusy">Запись уже ведёт другой матч.</param>
    public static CheckpointAction Decide(MatchRecord checkpoint, MatchRecord? saved, string? liveMatchId, bool recorderBusy)
    {
        // авария между сохранением итоговой записи и удалением контрольной: итоговая главнее
        if (saved != null && IsAtLeastAsComplete(saved, checkpoint)) return CheckpointAction.Discard;
        if (liveMatchId != null && liveMatchId == checkpoint.MatchId && !recorderBusy) return CheckpointAction.Resume;
        return CheckpointAction.SaveAsPartial;
    }
}

/// <summary>Какие матчи из очереди несохранённых ещё нужно записать.</summary>
public static class UnsavedQueuePolicy
{
    /// <summary>Убрать из очереди матчи, для которых в истории уже есть не менее полная версия (см. <see cref="CheckpointPolicy.IsAtLeastAsComplete"/>).</summary>
    public static List<MatchRecord> StillNeeded(IEnumerable<MatchRecord> pending, Func<string, MatchRecord?> findSaved) =>
        pending.Where(r => findSaved(r.Id) is not { } saved || !CheckpointPolicy.IsAtLeastAsComplete(saved, r)).ToList();
}

/// <summary>Отрезок игрового времени [From, To] (секунды), по которому приходили данные.</summary>
public sealed class CoverageSpan
{
    public int From { get; set; }
    public int To { get; set; }
}

/// <summary>Состояние матча из GSI, нужное для записи истории.</summary>
public sealed record MatchTick(
    string? MatchId,
    string? GameState,
    int? Clock,
    string? Team,
    string? WinTeam,
    int? HeroId,
    string? HeroKey,
    int? Level,
    int? Kills,
    int? Deaths,
    int? Assists,
    int? LastHits,
    int? Denies,
    int? Gpm,
    int? Xpm,
    int? Gold,
    IReadOnlyDictionary<string, string> Slots,
    Position Position,
    DateTime Utc,
    int? GoldUnreliable = null,
    int? BuybackCost = null,
    int? BuybackCooldown = null,
    int? HeroX = null,
    int? HeroY = null);

/// <summary>
/// Записывает свой матч по данным GSI: итоговую статистику, график раз в 30 секунд и время покупки предметов.
/// Демо и матчи без идентификатора не записываются.
/// </summary>
public sealed class MatchRecorder
{
    public const int SampleEverySec = 30;
    /// <summary>Пауза в данных дольше этого — пропуск: смерти за пропуск не получают места и времени.</summary>
    public const int GapSec = 20;

    /// <summary>Расходники и мелочь, которые не показываются в таймингах предметов.</summary>
    public static readonly IReadOnlySet<string> IgnoredItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "item_tango", "item_flask", "item_clarity", "item_enchanted_mango", "item_faerie_fire", "item_branches",
        "item_tpscroll", "item_ward_observer", "item_ward_sentry", "item_ward_dispenser", "item_smoke_of_deceit",
        "item_dust", "item_blood_grenade", "item_tango_single", "item_famango", "item_great_famango", "item_greater_famango",
        "item_aegis", "item_cheese", "item_refresher_shard", "item_roshans_banner"
    };

    /// <summary>Рецепт в инвентаре (item_recipe_…) — не предмет, а заготовка: в тайминги не пишем.</summary>
    public static bool IsRecipe(string? item) => item != null && item.StartsWith("item_recipe_", StringComparison.OrdinalIgnoreCase);

    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private int _lastSampleClock = int.MinValue;
    private MatchTick? _prev;

    public MatchRecord? Current { get; private set; }

    /// <summary>
    /// Продолжить матч из контрольной записи после перезапуска программы. Пропуск между записью и первым новым
    /// снимком отмечается в покрытии, смерти за это время станут «без места».
    /// </summary>
    public void Restore(MatchRecord checkpoint)
    {
        Current = checkpoint;
        Current.Resumed = true;
        _prev = null;
        _lastSampleClock = checkpoint.Samples.Count > 0 ? checkpoint.Samples.Max(x => x.Clock) : int.MinValue;
    }

    private static bool Stale(MatchTick prev, MatchTick now)
    {
        if (prev.Clock is int a && now.Clock is int b) return b - a > GapSec || b < a;
        return (now.Utc - prev.Utc).TotalSeconds > GapSec;
    }

    private static bool InProgress(string? state) => state is "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" or "DOTA_GAMERULES_STATE_PRE_GAME";
    private static bool PostGame(string? state) => state is "DOTA_GAMERULES_STATE_POST_GAME";

    /// <summary>Обработать снимок. Возвращает завершённый матч, который нужно сохранить, иначе null.</summary>
    public MatchRecord? Observe(MatchTick t)
    {
        if (string.IsNullOrWhiteSpace(t.MatchId) || t.MatchId == "0") return null;

        MatchRecord? completed = null;
        if (Current != null && Current.MatchId != t.MatchId) completed = Flush();
        if (_finished.Contains(t.MatchId)) return completed;

        if (Current == null && InProgress(t.GameState) && t.HeroId is > 0)
        {
            _prev = null;
            Current = new MatchRecord
            {
                Id = "gsi-" + t.MatchId,
                MatchId = t.MatchId,
                Source = "GSI",
                StartedUtc = t.Utc,
                HeroId = t.HeroId.Value,
                Position = t.Position,
                Ended = null
            };
            _lastSampleClock = int.MinValue;
        }
        if (Current == null) return completed;

        var r = Current;
        // был ли пропуск данных перед этим снимком (по покрытию: работает и после восстановления из контрольной записи)
        // первый снимок после старта игры (clock > 0) — запись началась не с начала: что уже в инвентаре, куплено раньше
        bool afterGap = t.Clock is int gc && (r.Coverage.Count > 0 ? gc - r.Coverage[^1].To > GapSec : gc > 0);
        // покрытие: продолжаем текущий отрезок или открываем новый после пропуска
        if (t.Clock is int c && (InProgress(t.GameState) || PostGame(t.GameState)))
        {
            var last = r.Coverage.Count > 0 ? r.Coverage[^1] : null;
            if (last != null && c >= last.To - 5 && c - last.To <= GapSec) last.To = Math.Max(last.To, c);
            else r.Coverage.Add(new CoverageSpan { From = c, To = c });
        }
        // смерти: счётчик GSI — истина о количестве. Место и время берём только если предыдущий снимок свежий
        // и смерть ровно одна; всё остальное (пропуск данных, перезапуск, скачок) — «смерть без места».
        if (t.Deaths is int dNow)
        {
            int missing = dNow - r.DeathEvents.Count - r.UnlocatedDeaths;
            if (missing > 0)
            {
                if (_prev is { } p && !Stale(p, t) && p.Deaths is int dPrev && dNow > dPrev && t.Clock is int deathClock)
                {
                    bool tp = p.Slots.Any(kv => (kv.Key.StartsWith("teleport", StringComparison.Ordinal) && kv.Value == "item_tpscroll")
                                                || (kv.Key.StartsWith("slot", StringComparison.Ordinal) && kv.Value is "item_travel_boots" or "item_travel_boots_2"));
                    bool? bb = p.BuybackCooldown is int cd && p.BuybackCost is int cost && p.Gold is int g ? cd == 0 && g >= cost : null;
                    r.DeathEvents.Add(new DeathEvent
                    {
                        Clock = deathClock,
                        GoldAtRisk = p.GoldUnreliable ?? p.Gold ?? 0,
                        HadTeleport = tp,
                        BuybackReady = bb,
                        Level = p.Level ?? 0,
                        X = p.HeroX,
                        Y = p.HeroY
                    });
                    missing--;
                }
                r.UnlocatedDeaths += missing;
            }
        }
        _prev = t;
        if (t.HeroId is > 0) r.HeroId = t.HeroId.Value;
        r.HeroKey = t.HeroKey ?? r.HeroKey;
        if (t.Team is "radiant" or "dire") r.Team = t.Team;
        r.Kills = t.Kills ?? r.Kills;
        r.Deaths = t.Deaths ?? r.Deaths;
        r.Assists = t.Assists ?? r.Assists;
        r.LastHits = t.LastHits ?? r.LastHits;
        r.Denies = t.Denies ?? r.Denies;
        r.Gpm = t.Gpm ?? r.Gpm;
        r.Xpm = t.Xpm ?? r.Xpm;
        r.Level = t.Level ?? r.Level;

        if (t.Clock is int clock)
        {
            if (clock > r.DurationSec) r.DurationSec = clock;
            if (InProgress(t.GameState))
            {
                if (clock >= 0 && (_lastSampleClock == int.MinValue || clock - _lastSampleClock >= SampleEverySec))
                {
                    _lastSampleClock = clock;
                    r.Samples.Add(new MatchSample { Clock = clock, Gold = t.Gold, Gpm = t.Gpm, Xpm = t.Xpm, LastHits = t.LastHits, Level = t.Level });
                }
                foreach (var kv in t.Slots)
                {
                    if (!(kv.Key.StartsWith("slot", StringComparison.Ordinal) || kv.Key.StartsWith("stash", StringComparison.Ordinal))) continue;
                    var item = kv.Value;
                    if (item == "empty" || IgnoredItems.Contains(item) || IsRecipe(item) || r.ItemTimings.ContainsKey(item)) continue;
                    r.ItemTimings[item] = Math.Max(0, clock);
                    if (afterGap && !r.IsApproxTiming(item)) r.ApproxItems.Add(item);   // куплен где-то в пропуске
                }
            }
        }

        if (PostGame(t.GameState))
        {
            if (t.WinTeam is "radiant" or "dire" && r.Team != null)
                r.Result = t.WinTeam == r.Team ? MatchResult.Win : MatchResult.Loss;
            r.Ended = true;
            completed = Flush();
        }
        return completed;
    }

    /// <summary>Завершить текущий матч (смена матча, выход из приложения). Слишком короткие матчи не сохраняются.</summary>
    public MatchRecord? Flush()
    {
        var r = Current;
        Current = null;
        _prev = null;
        if (r == null) return null;
        if (r.MatchId != null) _finished.Add(r.MatchId);
        r.Ended ??= false;
        return r.DurationSec >= 120 ? r : null;
    }
}

/// <summary>
/// Хранилище истории: по одному JSON-файлу на матч в %AppData%\Roshiq\matches.
/// 0.17.5: запись атомарная (временный файл + замена), <see cref="Save"/> сообщает, удалось ли сохранить,
/// кэш меняется только после успешной записи и хранит собственную копию записи (правки объекта вызывающим кодом
/// не попадают в «сохранённое» в обход диска). Повреждённые файлы откладываются в «.bad», а не пропадают молча.
/// </summary>
public sealed class MatchHistoryStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public const string CheckpointFile = "_current.checkpoint";

    private readonly string _dir;
    private readonly object _sync = new();
    // кэш: запись (только для порядка и поиска) + её JSON; наружу всегда отдаётся новая копия из JSON
    private List<(MatchRecord Meta, string Json)>? _cache;
    private readonly List<string> _quarantined = new();

    public MatchHistoryStore(string directory) { _dir = directory; }

    public event EventHandler? Changed;

    /// <summary>Файлы истории, которые не удалось прочитать и которые переименованы в «.bad» (для диагностики).</summary>
    public IReadOnlyList<string> Quarantined { get { lock (_sync) return _quarantined.ToList(); } }

    /// <summary>Независимая копия записи (через JSON).</summary>
    public static MatchRecord Clone(MatchRecord r) =>
        JsonSerializer.Deserialize<MatchRecord>(JsonSerializer.Serialize(r, Json), Json)!;

    /// <summary>
    /// Все матчи, новые сверху. Каждый вызов возвращает новые независимые копии: изменения этих объектов не видны
    /// ни другим читателям, ни кэшу, ни диску — менять историю можно только через <see cref="Save"/>.
    /// </summary>
    public IReadOnlyList<MatchRecord> All()
    {
        lock (_sync)
        {
            _cache ??= LoadFromDisk();
            return _cache.OrderByDescending(e => e.Meta.StartedUtc).Select(e => FromJson(e.Json)).ToList().AsReadOnly();
        }
    }

    /// <summary>Запись по Id (независимая копия) или null.</summary>
    public MatchRecord? Find(string id)
    {
        lock (_sync)
        {
            _cache ??= LoadFromDisk();
            foreach (var e in _cache) if (e.Meta.Id == id) return FromJson(e.Json);
            return null;
        }
    }

    private static MatchRecord FromJson(string json) => JsonSerializer.Deserialize<MatchRecord>(json, Json)!;

    private List<(MatchRecord Meta, string Json)> LoadFromDisk()
    {
        var list = new List<(MatchRecord, string)>();
        try
        {
            if (!Directory.Exists(_dir)) return list;
            // недописанные временные файлы — следы оборванной записи; прежняя версия матча цела
            foreach (var tmp in Directory.GetFiles(_dir, "*" + AtomicFile.TempSuffix))
                try { File.Delete(tmp); } catch { }
            foreach (var file in Directory.GetFiles(_dir, "*.json"))
            {
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var text = File.ReadAllText(file);
                    var r = JsonSerializer.Deserialize<MatchRecord>(text, Json);
                    if (r != null && !string.IsNullOrWhiteSpace(r.Id)) list.Add((r, text));
                    else Quarantine(file, "пустая запись");
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { Quarantine(file, ex.Message); }
                catch (Exception ex) { Log.Warn($"История: не прочитан файл {Path.GetFileName(file)}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn("История матчей недоступна: " + ex.Message); }
        return list;
    }

    private void Quarantine(string file, string reason)
    {
        var target = file + ".bad";
        try
        {
            if (File.Exists(target)) target = file + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bad";
            File.Move(file, target);
            _quarantined.Add(Path.GetFileName(target));
            Log.Warn($"История: повреждённый файл {Path.GetFileName(file)} отложен как {Path.GetFileName(target)} ({reason})");
        }
        catch (Exception ex) { Log.Warn($"История: повреждённый файл {Path.GetFileName(file)} ({reason}), отложить не удалось: {ex.Message}"); }
    }

    private string FileFor(string id) =>
        Path.Combine(_dir, new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()) + ".json");

    /// <summary>
    /// Сохранить матч. true — запись на диске; false — не удалось (кэш не изменён, причина в логе).
    /// Повторное сохранение того же Id заменяет прежнюю версию.
    /// </summary>
    public bool Save(MatchRecord record)
    {
        lock (_sync)
        {
            _cache ??= LoadFromDisk();
            string json;
            try
            {
                json = JsonSerializer.Serialize(record, Json);
                AtomicFile.WriteAllText(FileFor(record.Id), json);
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось сохранить матч {record.Id}: {ex.Message}");
                return false;
            }
            _cache.RemoveAll(x => x.Meta.Id == record.Id);
            _cache.Add((FromJson(json), json));
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Delete(string id)
    {
        lock (_sync)
        {
            _cache ??= LoadFromDisk();
            _cache.RemoveAll(x => x.Meta.Id == id);
            try { if (File.Exists(FileFor(id))) File.Delete(FileFor(id)); }
            catch (Exception ex) { Log.Warn("Не удалось удалить матч: " + ex.Message); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ очередь несохранённых матчей

    public const string PendingFile = "_unsaved.pending";
    private string PendingPath => Path.Combine(_dir, PendingFile);

    /// <summary>
    /// Запомнить матчи, которые не удалось записать (чтобы повторить и после перезапуска). Пустой список — удалить файл.
    /// Пишется отдельным файлом: сбой одного файла матча не мешает записать очередь.
    /// </summary>
    public bool SavePending(IReadOnlyCollection<MatchRecord> pending)
    {
        try
        {
            lock (_sync)
            {
                if (pending.Count == 0) { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
                else AtomicFile.WriteAllText(PendingPath, JsonSerializer.Serialize(pending, Json));
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Очередь несохранённых матчей не записана: " + ex.Message);
            return false;
        }
    }

    public IReadOnlyList<MatchRecord> LoadPending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return Array.Empty<MatchRecord>();
            return JsonSerializer.Deserialize<List<MatchRecord>>(File.ReadAllText(PendingPath), Json)?.Where(r => !string.IsNullOrWhiteSpace(r.Id)).ToList()
                   ?? (IReadOnlyList<MatchRecord>)Array.Empty<MatchRecord>();
        }
        catch (Exception ex)
        {
            Log.Warn("Очередь несохранённых матчей не читается: " + ex.Message);
            try { File.Move(PendingPath, PendingPath + ".bad", overwrite: true); } catch { }
            return Array.Empty<MatchRecord>();
        }
    }

    // ------------------------------------------------------------------ контрольная запись текущего матча

    private string CheckpointPath => Path.Combine(_dir, CheckpointFile);

    /// <summary>Записать текущий (незаконченный) матч, чтобы продолжить его после перезапуска программы.</summary>
    public bool SaveCheckpoint(MatchRecord current)
    {
        try
        {
            lock (_sync) AtomicFile.WriteAllText(CheckpointPath, JsonSerializer.Serialize(current, Json));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Контрольная запись матча не сохранена: " + ex.Message);
            return false;
        }
    }

    /// <summary>Контрольная запись и время её сохранения (UTC), если есть и читается.</summary>
    public (MatchRecord Record, DateTime SavedUtc)? LoadCheckpoint()
    {
        try
        {
            if (!File.Exists(CheckpointPath)) return null;
            var r = JsonSerializer.Deserialize<MatchRecord>(File.ReadAllText(CheckpointPath), Json);
            if (r == null || string.IsNullOrWhiteSpace(r.Id)) { ClearCheckpoint(); return null; }
            return (r, File.GetLastWriteTimeUtc(CheckpointPath));
        }
        catch (Exception ex)
        {
            Log.Warn("Контрольная запись матча не читается: " + ex.Message);
            try { File.Move(CheckpointPath, CheckpointPath + ".bad", overwrite: true); } catch { }
            return null;
        }
    }

    /// <summary>Удалить контрольную запись (матч сохранён). Если указан id — только если запись того же матча.</summary>
    public void ClearCheckpoint(string? id = null)
    {
        try
        {
            if (!File.Exists(CheckpointPath)) return;
            if (id != null)
            {
                var r = JsonSerializer.Deserialize<MatchRecord>(File.ReadAllText(CheckpointPath), Json);
                if (r != null && r.Id != id) return;
            }
            File.Delete(CheckpointPath);
        }
        catch (Exception ex) { Log.Warn("Контрольная запись матча не удалена: " + ex.Message); }
    }

    /// <summary>
    /// Добавить матчи из OpenDota. Матч, уже записанный по GSI, не дублируется: у него только
    /// дополняется итог, если он был неизвестен. Возвращает число новых матчей.
    /// </summary>
    public int Import(IEnumerable<RecentMatchRow> rows)
    {
        int added = 0;
        var existing = All();
        foreach (var row in rows)
        {
            var mid = row.MatchId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var gsi = existing.FirstOrDefault(x => x.MatchId == mid);
            var result = row.Won switch { true => MatchResult.Win, false => MatchResult.Loss, _ => MatchResult.Unknown };
            if (gsi != null)
            {
                if (gsi.Result == MatchResult.Unknown && result != MatchResult.Unknown)
                {
                    var copy = Clone(gsi);       // правим копию: кэш меняется только через успешный Save
                    copy.Result = result;
                    Save(copy);
                }
                continue;
            }
            if (Save(new MatchRecord
            {
                Id = "od-" + mid,
                MatchId = mid,
                Source = "OpenDota",
                StartedUtc = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, row.StartTime)).UtcDateTime,
                HeroId = row.HeroId,
                Team = row.IsRadiant ? "radiant" : "dire",
                Result = result,
                DurationSec = row.Duration,
                Kills = row.Kills,
                Deaths = row.Deaths,
                Assists = row.Assists,
                LastHits = row.LastHits,
                Gpm = row.GoldPerMin,
                Xpm = row.XpPerMin
            })) added++;
        }
        return added;
    }
}

public sealed record GroupStats(string Key, int Games, int Wins, int Losses, double AvgGpm, double AvgXpm, double AvgKills, double AvgDeaths, double AvgAssists)
{
    /// <summary>Доля побед только среди матчей с известным итогом; null, если итогов нет.</summary>
    public double? WinRate => Wins + Losses == 0 ? null : (double)Wins / (Wins + Losses);
}

/// <summary>Медианный тайминг предмета. SameHero — выборка «тот же герой в той же роли», иначе «та же роль».</summary>
public sealed record ItemBenchmark(int AvgSec, int Matches, bool SameHero);

/// <summary>Статистика по собственной истории. Ничего не выдумывается: только ваши записанные матчи.</summary>
public static class MatchStats
{
    private static GroupStats Group(string key, IReadOnlyCollection<MatchRecord> g) => new(
        key, g.Count,
        g.Count(m => m.Result == MatchResult.Win),
        g.Count(m => m.Result == MatchResult.Loss),
        g.Average(m => (double)m.Gpm), g.Average(m => (double)m.Xpm),
        g.Average(m => (double)m.Kills), g.Average(m => (double)m.Deaths), g.Average(m => (double)m.Assists));

    public static IReadOnlyList<(int HeroId, GroupStats Stats)> ByHero(IEnumerable<MatchRecord> records) =>
        records.Where(m => m.HeroId > 0).GroupBy(m => m.HeroId)
            .Select(g => (g.Key, Group(g.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), g.ToList())))
            .OrderByDescending(x => x.Item2.Games).ThenBy(x => x.Key).ToList();

    public static IReadOnlyList<(Position Position, GroupStats Stats)> ByPosition(IEnumerable<MatchRecord> records) =>
        records.Where(m => m.Position != null).GroupBy(m => m.Position!.Value)
            .Select(g => (g.Key, Group(g.Key.ToString(), g.ToList())))
            .OrderBy(x => x.Key).ToList();

    /// <summary>Сколько точных наблюдений нужно, чтобы начать сравнение (порог начала, а не «высокая уверенность»).</summary>
    public const int MinBenchmarkMatches = 3;

    /// <summary>
    /// Медианный тайминг предмета по другим вашим матчам. Роль текущего матча должна быть известна:
    /// сначала тот же герой в той же роли, иначе та же роль. Матчи с неизвестной ролью и тайминги,
    /// увиденные после пропуска данных, не участвуют. Меньше 3 точных наблюдений — сравнения нет.
    /// <see cref="ItemBenchmark.SameHero"/> = true означает «тот же герой и та же роль».
    /// </summary>
    public static ItemBenchmark? Benchmark(IEnumerable<MatchRecord> records, MatchRecord current, string itemKey)
    {
        if (current.Position is not Position role) return null;
        var exact = records.Where(m => m.Id != current.Id && m.Position == role
                                       && m.ItemTimings.ContainsKey(itemKey) && !m.IsApproxTiming(itemKey)).ToList();
        var heroRole = exact.Where(m => m.HeroId == current.HeroId).ToList();
        if (heroRole.Count >= MinBenchmarkMatches)
            return new ItemBenchmark((int)Math.Round(Median(heroRole.Select(m => m.ItemTimings[itemKey]))), heroRole.Count, true);
        if (exact.Count >= MinBenchmarkMatches)
            return new ItemBenchmark((int)Math.Round(Median(exact.Select(m => m.ItemTimings[itemKey]))), exact.Count, false);
        return null;
    }

    /// <summary>Медиана: одна неудачная игра не сдвигает «обычное» так, как сдвигает среднее.</summary>
    public static double Median(IEnumerable<int> values)
    {
        var list = values.OrderBy(v => v).ToList();
        if (list.Count == 0) return 0;
        int mid = list.Count / 2;
        return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2.0;
    }

    /// <summary>
    /// Слишком большой разброс, чтобы называть медиану «обычным»: межквартильный размах больше 60% медианы.
    /// Тогда сравнение не показываем — честнее промолчать, чем сравнить с шумом.
    /// </summary>
    public static bool IsNoisy(IReadOnlyCollection<int> values)
    {
        if (values.Count < 3) return true;
        var list = values.OrderBy(v => v).ToList();
        double Q(double p)
        {
            double pos = p * (list.Count - 1);
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            return list[lo] + (list[hi] - list[lo]) * (pos - lo);
        }
        double med = Median(list);
        return med <= 0 || (Q(0.75) - Q(0.25)) / med > 0.6;
    }

    /// <summary>Сколько раз в среднем за матч срабатывала каждая подсказка (по матчам, записанным через GSI).</summary>
    public static IReadOnlyList<(string Key, double PerMatch, int Total)> AlertAverages(IEnumerable<MatchRecord> records)
    {
        var gsi = records.Where(m => m.FromGsi).ToList();
        if (gsi.Count == 0) return Array.Empty<(string, double, int)>();
        return gsi.SelectMany(m => m.Alerts).GroupBy(kv => kv.Key)
            .Select(g => (g.Key, (double)g.Sum(x => x.Value) / gsi.Count, g.Sum(x => x.Value)))
            .OrderByDescending(x => x.Item2).ToList();
    }
}
