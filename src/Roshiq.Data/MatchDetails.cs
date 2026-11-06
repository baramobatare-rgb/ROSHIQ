using System.Globalization;
using System.Text.Json;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Data;

/// <summary>Сравнение с другими игроками на этом герое (OpenDota benchmarks): raw — ваше значение, pct — доля игроков ниже вас (0…1).</summary>
public sealed record Benchmark(string Key, double Raw, double Pct);

/// <summary>Покупка предмета (время в секундах игрового времени, ключ item_xxx).</summary>
public sealed record Purchase(int Time, string ItemKey);

/// <summary>Ваша смерть: время и герой-убийца (0 — не герой: крипы, башня, нейтралы).</summary>
public sealed record DeathBy(int Time, int KillerHeroId);

/// <summary>Вард: где поставлен (0…1 по ширине/высоте карты, 0,0 — левый нижний угол), когда, сколько прожил, снят ли врагом.</summary>
public sealed record WardRecord(double X, double Y, int Placed, int? Lifetime, bool Dewarded);

/// <summary>Разобранный OpenDota матч — только данные одного игрока (вас).</summary>
public sealed class MatchDetails
{
    public long MatchId { get; init; }
    /// <summary>Запись матча разобрана (есть графики, покупки, варды). Иначе есть только итог.</summary>
    public bool Parsed { get; init; }
    public int Duration { get; init; }
    public bool? Won { get; init; }
    public int HeroId { get; init; }
    public bool IsRadiant { get; init; }
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public IReadOnlyList<Benchmark> Benchmarks { get; init; } = Array.Empty<Benchmark>();
    /// <summary>Добивания/денаи по минутам (индекс — минута).</summary>
    public IReadOnlyList<int> LastHitsByMinute { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> DeniesByMinute { get; init; } = Array.Empty<int>();
    public IReadOnlyList<Purchase> Purchases { get; init; } = Array.Empty<Purchase>();
    public IReadOnlyList<DeathBy> DeathsBy { get; init; } = Array.Empty<DeathBy>();
    public IReadOnlyList<WardRecord> Observers { get; init; } = Array.Empty<WardRecord>();
    public IReadOnlyList<WardRecord> Sentries { get; init; } = Array.Empty<WardRecord>();
    /// <summary>Эффективность линии 0…100 (доля от максимально возможного золота с линии к 10:00).</summary>
    public double? LaneEfficiency { get; init; }
    public int? CampsStacked { get; init; }

    public int? LastHitsAt(int minute) => minute < LastHitsByMinute.Count ? LastHitsByMinute[minute] : null;
    public int? DeniesAt(int minute) => minute < DeniesByMinute.Count ? DeniesByMinute[minute] : null;

    /// <summary>
    /// Разобрать ответ OpenDota /matches/{id} для игрока accountId.
    /// heroNpc — npc-имя героя по id («npc_dota_hero_axe»), нужно, чтобы найти ваши смерти в журналах убийств.
    /// </summary>
    public static MatchDetails? Parse(JsonElement root, long accountId, Func<int, string?> heroNpc)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            return null;
        JsonElement? me = null;
        foreach (var p in players.EnumerateArray())
            if (Long(p, "account_id") == accountId) { me = p; break; }
        if (me is not JsonElement m) return null;

        int heroId = Int(m, "hero_id") ?? 0;
        int slot = Int(m, "player_slot") ?? 0;
        bool radiant = slot < 128;
        bool? radiantWin = root.TryGetProperty("radiant_win", out var rw) && rw.ValueKind is JsonValueKind.True or JsonValueKind.False ? rw.GetBoolean() : null;
        bool parsed = (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number)
                      || (m.TryGetProperty("lh_t", out var lht) && lht.ValueKind == JsonValueKind.Array && lht.GetArrayLength() > 0);

        var benchmarks = new List<Benchmark>();
        if (m.TryGetProperty("benchmarks", out var b) && b.ValueKind == JsonValueKind.Object)
            foreach (var prop in b.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Object && Dbl(prop.Value, "raw") is double raw && Dbl(prop.Value, "pct") is double pct)
                    benchmarks.Add(new Benchmark(prop.Name, raw, Math.Clamp(pct, 0, 1)));

        var purchases = new List<Purchase>();
        if (m.TryGetProperty("purchase_log", out var pl) && pl.ValueKind == JsonValueKind.Array)
            foreach (var e in pl.EnumerateArray())
                if (Int(e, "time") is int t && Str(e, "key") is string key && key.Length > 0)
                    purchases.Add(new Purchase(t, key.StartsWith("item_", StringComparison.Ordinal) ? key : "item_" + key));

        // Кто вас убивал: ваш npc-герой в kills_log других игроков
        var deaths = new List<DeathBy>();
        if (heroNpc(heroId) is string myNpc)
            foreach (var p in players.EnumerateArray())
            {
                if (Long(p, "account_id") == accountId && Int(p, "player_slot") == slot) continue;
                if (!p.TryGetProperty("kills_log", out var kl) || kl.ValueKind != JsonValueKind.Array) continue;
                int killer = Int(p, "hero_id") ?? 0;
                foreach (var k in kl.EnumerateArray())
                    if (Str(k, "key") == myNpc && Int(k, "time") is int t) deaths.Add(new DeathBy(t, killer));
            }

        return new MatchDetails
        {
            MatchId = Long(root, "match_id") ?? 0,
            Parsed = parsed,
            Duration = Int(root, "duration") ?? 0,
            Won = radiantWin == null ? null : radiantWin == radiant,
            HeroId = heroId,
            IsRadiant = radiant,
            Kills = Int(m, "kills") ?? 0,
            Deaths = Int(m, "deaths") ?? 0,
            Assists = Int(m, "assists") ?? 0,
            Benchmarks = benchmarks,
            LastHitsByMinute = IntArray(m, "lh_t"),
            DeniesByMinute = IntArray(m, "dn_t"),
            Purchases = purchases,
            DeathsBy = deaths.OrderBy(d => d.Time).ToList(),
            Observers = Wards(m, "obs_log", "obs_left_log"),
            Sentries = Wards(m, "sen_log", "sen_left_log"),
            LaneEfficiency = Dbl(m, "lane_efficiency_pct"),
            CampsStacked = Int(m, "camps_stacked"),
        };
    }

    /// <summary>Варды: координаты OpenDota в клетках 64…192 → 0…1; время жизни — по ehandle из журнала снятых.</summary>
    private static IReadOnlyList<WardRecord> Wards(JsonElement m, string placedKey, string leftKey)
    {
        if (!m.TryGetProperty(placedKey, out var placed) || placed.ValueKind != JsonValueKind.Array) return Array.Empty<WardRecord>();
        var left = new Dictionary<long, (int Time, bool Dewarded)>();
        if (m.TryGetProperty(leftKey, out var l) && l.ValueKind == JsonValueKind.Array)
            foreach (var e in l.EnumerateArray())
                if (Long(e, "ehandle") is long h && Int(e, "time") is int t)
                    left[h] = (t, Str(e, "attackername") is string a && a.Length > 0);
        var result = new List<WardRecord>();
        foreach (var e in placed.EnumerateArray())
        {
            if (Int(e, "time") is not int t || Dbl(e, "x") is not double x || Dbl(e, "y") is not double y) continue;
            int? life = null;
            bool dewarded = false;
            if (Long(e, "ehandle") is long h && left.TryGetValue(h, out var gone)) { life = Math.Max(0, gone.Time - t); dewarded = gone.Dewarded; }
            result.Add(new WardRecord(Math.Clamp((x - 64) / 128.0, 0, 1), Math.Clamp((y - 64) / 128.0, 0, 1), t, life, dewarded));
        }
        return result;
    }

    private static IReadOnlyList<int> IntArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0).ToList()
            : Array.Empty<int>();

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        } : null;

    internal static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)v.GetDouble(),
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null
        } : null;

    internal static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var i) => i,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null
        } : null;

    internal static double? Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) => s,
            _ => null
        } : null;
}

/// <summary>Нормы таймингов: медианное время покупки предмета на герое по OpenDota (scenarios/itemTimings). Кэш на неделю.</summary>
public sealed class ItemTimingNormCache
{
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string, NormEntry> _norms = new(StringComparer.OrdinalIgnoreCase);

    public ItemTimingNormCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                _norms = new Dictionary<string, NormEntry>(
                    JsonSerializer.Deserialize<Dictionary<string, NormEntry>>(File.ReadAllText(path)) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Warn("Кэш таймингов предметов повреждён: " + ex.Message); }
    }

    private static string Key(int heroId, string itemKey) => heroId + ":" + itemKey;

    /// <summary>Норма (сек) или null; stale = true, если её пора обновить.</summary>
    public int? Get(int heroId, string itemKey, out bool stale)
    {
        lock (_sync)
        {
            if (_norms.TryGetValue(Key(heroId, itemKey), out var n))
            {
                stale = DateTime.UtcNow - n.FetchedUtc > TimeSpan.FromDays(7);
                return n.Seconds;
            }
        }
        stale = true;
        return null;
    }

    public void Set(int heroId, string itemKey, int? seconds)
    {
        lock (_sync)
        {
            _norms[Key(heroId, itemKey)] = new NormEntry { Seconds = seconds, FetchedUtc = DateTime.UtcNow };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_norms));
            }
            catch (Exception ex) { Log.Warn("Не удалось сохранить тайминги предметов: " + ex.Message); }
        }
    }

    /// <summary>Взвешенная медиана по корзинам времени из ответа scenarios/itemTimings.</summary>
    public static int? Median(IEnumerable<(int Time, int Games)> buckets)
    {
        var list = buckets.Where(b => b.Games > 0 && b.Time > 0).OrderBy(b => b.Time).ToList();
        long total = list.Sum(b => (long)b.Games);
        if (total < 50) return null; // слишком мало данных — нормы нет
        long acc = 0;
        foreach (var b in list)
        {
            acc += b.Games;
            if (acc * 2 >= total) return b.Time;
        }
        return null;
    }

    public sealed class NormEntry
    {
        public int? Seconds { get; set; }
        public DateTime FetchedUtc { get; set; }
    }
}
