using System.Text.Json;
using System.Text.Json.Serialization;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;
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
                Position = t.Position
            };
            _lastSampleClock = int.MinValue;
        }
        if (Current == null) return completed;

        var r = Current;
        // смерть: счётчик смертей вырос — состояние берём из предыдущего снимка (до потери золота)
        if (_prev != null && t.Deaths is int dNow && _prev.Deaths is int dPrev && dNow > dPrev && t.Clock is int deathClock)
        {
            var p = _prev;
            bool tp = p.Slots.Any(kv => (kv.Key.StartsWith("teleport", StringComparison.Ordinal) && kv.Value == "item_tpscroll")
                                        || (kv.Key.StartsWith("slot", StringComparison.Ordinal) && kv.Value is "item_travel_boots" or "item_travel_boots_2"));
            bool? bb = p.BuybackCooldown is int cd && p.BuybackCost is int cost && p.Gold is int g ? cd == 0 && g >= cost : null;
            for (int i = 0; i < Math.Min(3, dNow - dPrev); i++)
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
                }
            }
        }

        if (PostGame(t.GameState))
        {
            if (t.WinTeam is "radiant" or "dire" && r.Team != null)
                r.Result = t.WinTeam == r.Team ? MatchResult.Win : MatchResult.Loss;
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
        return r.DurationSec >= 120 ? r : null;
    }
}

/// <summary>Хранилище истории: по одному JSON-файлу на матч в %AppData%\Roshiq\matches.</summary>
public sealed class MatchHistoryStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dir;
    private readonly object _sync = new();
    private List<MatchRecord>? _cache;

    public MatchHistoryStore(string directory) { _dir = directory; }

    public event EventHandler? Changed;

    public IReadOnlyList<MatchRecord> All()
    {
        lock (_sync)
        {
            _cache ??= LoadFromDisk();
            return _cache.OrderByDescending(m => m.StartedUtc).ToList();
        }
    }

    private List<MatchRecord> LoadFromDisk()
    {
        var list = new List<MatchRecord>();
        try
        {
            if (!Directory.Exists(_dir)) return list;
            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<MatchRecord>(File.ReadAllText(file), Json);
                    if (r != null && !string.IsNullOrWhiteSpace(r.Id)) list.Add(r);
                }
                catch (Exception ex) { Log.Warn($"История: пропущен файл {Path.GetFileName(file)}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn("История матчей недоступна: " + ex.Message); }
        return list;
    }

    private string FileFor(string id) =>
        Path.Combine(_dir, new string(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray()) + ".json");

    public void Save(MatchRecord record)
    {
        lock (_sync)
        {
            _cache ??= LoadFromDisk();
            _cache.RemoveAll(x => x.Id == record.Id);
            _cache.Add(record);
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(FileFor(record.Id), JsonSerializer.Serialize(record, Json));
            }
            catch (Exception ex) { Log.Warn("Не удалось сохранить матч: " + ex.Message); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(string id)
    {
        lock (_sync)
        {
            _cache ??= LoadFromDisk();
            _cache.RemoveAll(x => x.Id == id);
            try { if (File.Exists(FileFor(id))) File.Delete(FileFor(id)); }
            catch (Exception ex) { Log.Warn("Не удалось удалить матч: " + ex.Message); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
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
                    gsi.Result = result;
                    Save(gsi);
                }
                continue;
            }
            Save(new MatchRecord
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
            });
            added++;
        }
        return added;
    }
}

public sealed record GroupStats(string Key, int Games, int Wins, int Losses, double AvgGpm, double AvgXpm, double AvgKills, double AvgDeaths, double AvgAssists)
{
    /// <summary>Доля побед только среди матчей с известным итогом; null, если итогов нет.</summary>
    public double? WinRate => Wins + Losses == 0 ? null : (double)Wins / (Wins + Losses);
}

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

    /// <summary>Средний тайминг предмета по другим вашим матчам: сначала на этом герое, иначе на этой позиции.</summary>
    public static ItemBenchmark? Benchmark(IEnumerable<MatchRecord> records, MatchRecord current, string itemKey)
    {
        var others = records.Where(m => m.Id != current.Id && m.ItemTimings.ContainsKey(itemKey)).ToList();
        var sameHero = others.Where(m => m.HeroId == current.HeroId).ToList();
        if (sameHero.Count > 0) return new ItemBenchmark((int)Math.Round(Median(sameHero.Select(m => m.ItemTimings[itemKey]))), sameHero.Count, true);
        var samePos = others.Where(m => current.Position != null && m.Position == current.Position).ToList();
        if (samePos.Count > 0) return new ItemBenchmark((int)Math.Round(Median(samePos.Select(m => m.ItemTimings[itemKey]))), samePos.Count, false);
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
