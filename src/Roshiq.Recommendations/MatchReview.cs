using System.Text.Json;
using System.Text.Json.Serialization;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;
using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Вывод разбора: severity &lt; 0 — проблема (чем меньше, тем важнее), &gt; 0 — что получилось.</summary>
public sealed record Insight(string Kind, int Severity, string Ru, string En)
{
    public string Text(Language lang) => lang == Language.English ? En : Ru;
}

public sealed record ItemTimingRow(string ItemKey, int Yours, int? Norm)
{
    public int? Delta => Norm is int n ? Yours - n : null;
}

public sealed record KillerRow(int HeroId, int Count);

/// <summary>Разбор одного матча по данным OpenDota (сохраняется локально).</summary>
public sealed class MatchReview
{
    public string MatchId { get; set; } = "";
    public int HeroId { get; set; }
    public Position? Position { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool Parsed { get; set; }
    public List<Benchmark> Benchmarks { get; set; } = new();
    public int? LastHits10 { get; set; }
    public int? Denies10 { get; set; }
    /// <summary>Ориентир добиваний к 10:00 для роли (не статистика, а понятная планка).</summary>
    public int? LastHits10Target { get; set; }
    public double? LaneEfficiency { get; set; }
    public List<ItemTimingRow> Items { get; set; } = new();
    public List<KillerRow> Killers { get; set; } = new();
    public List<WardRecord> Observers { get; set; } = new();
    public int Sentries { get; set; }
    public List<Insight> Insights { get; set; } = new();

    [JsonIgnore] public IEnumerable<Insight> Problems => Insights.Where(i => i.Severity < 0).OrderBy(i => i.Severity);
    [JsonIgnore] public IEnumerable<Insight> Good => Insights.Where(i => i.Severity > 0).OrderByDescending(i => i.Severity);

    /// <summary>Главные выводы: до 2 проблем и 1 плюс.</summary>
    [JsonIgnore] public IReadOnlyList<Insight> Top => Problems.Take(2).Concat(Good.Take(1)).ToList();

    public double? ObserverAvgLife => Observers.Count(w => w.Lifetime != null) is int n and > 0
        ? Observers.Where(w => w.Lifetime != null).Average(w => w.Lifetime!.Value) : null;
}

/// <summary>Делает выводы из разобранного матча. Только правила, никаких выдуманных цифр: нормы — из OpenDota или явно названные ориентиры.</summary>
public static class MatchReviewBuilder
{
    /// <summary>Ориентир добиваний к 10:00 по роли (для саппортов не задаётся).</summary>
    public static int? LastHitTarget(Position? p) => p switch
    {
        Position.Carry => 50,
        Position.Mid => 45,
        Position.Offlane => 30,
        _ => null
    };

    public static readonly IReadOnlyDictionary<string, (string Ru, string En)> BenchmarkNames = new Dictionary<string, (string, string)>
    {
        ["gold_per_min"] = ("Золото в минуту", "Gold per minute"),
        ["xp_per_min"] = ("Опыт в минуту", "XP per minute"),
        ["last_hits_per_min"] = ("Добивания в минуту", "Last hits per minute"),
        ["hero_damage_per_min"] = ("Урон по героям в минуту", "Hero damage per minute"),
        ["hero_healing_per_min"] = ("Лечение в минуту", "Healing per minute"),
        ["tower_damage"] = ("Урон по строениям", "Building damage"),
        ["kills_per_min"] = ("Убийства в минуту", "Kills per minute"),
    };

    /// <summary>Крупные предметы для сравнения таймингов: первые покупки дороже minCost (без расходников).</summary>
    public static IReadOnlyList<Purchase> KeyPurchases(MatchDetails d, Func<string, int?> cost, int minCost = 2000, int max = 4)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return d.Purchases.Where(p => p.Time > 0 && cost(p.ItemKey) is int c && c >= minCost && seen.Add(p.ItemKey)).Take(max).ToList();
    }

    public static MatchReview Build(MatchDetails d, Position? position, IReadOnlyDictionary<string, int?> norms,
        Func<string, (string Ru, string En)> itemName, Func<int, (string Ru, string En)> heroName, Func<string, int?> cost)
    {
        var r = new MatchReview
        {
            MatchId = d.MatchId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HeroId = d.HeroId,
            Position = position,
            CreatedUtc = DateTime.UtcNow,
            Parsed = d.Parsed,
            Benchmarks = d.Benchmarks.Where(b => BenchmarkNames.ContainsKey(b.Key) && !(b.Raw == 0 && b.Key is "hero_healing_per_min" or "tower_damage")).ToList(),
            LastHits10 = d.LastHitsAt(10),
            Denies10 = d.DeniesAt(10),
            LastHits10Target = LastHitTarget(position),
            LaneEfficiency = d.LaneEfficiency,
            Observers = d.Observers.ToList(),
            Sentries = d.Sentries.Count,
        };
        r.Items = KeyPurchases(d, cost).Select(p => new ItemTimingRow(p.ItemKey, p.Time, norms.TryGetValue(p.ItemKey, out var n) ? n : null)).ToList();
        r.Killers = d.DeathsBy.Where(x => x.KillerHeroId > 0).GroupBy(x => x.KillerHeroId)
            .Select(g => new KillerRow(g.Key, g.Count())).OrderByDescending(k => k.Count).ToList();

        var ins = r.Insights;
        bool core = position is Position.Carry or Position.Mid or Position.Offlane;

        // 1. Сравнение с игроками на этом герое
        foreach (var b in r.Benchmarks)
        {
            var (ru, en) = BenchmarkNames[b.Key];
            int pct = (int)Math.Round(b.Pct * 100);
            if (b.Pct < 0.3)
                ins.Add(new Insight("benchmark", b.Pct < 0.15 ? -3 : -2,
                    $"{ru}: хуже, чем у {100 - pct}% игроков на этом герое ({Raw(b)})",
                    $"{en}: lower than {100 - pct}% of players on this hero ({Raw(b)})"));
            else if (b.Pct >= 0.75)
                ins.Add(new Insight("benchmark", b.Pct >= 0.9 ? 2 : 1,
                    $"{ru}: лучше, чем у {pct}% игроков на этом герое ({Raw(b)})",
                    $"{en}: better than {pct}% of players on this hero ({Raw(b)})"));
        }

        // 2. Линия
        if (core && r.LastHits10 is int lh && r.LastHits10Target is int target)
        {
            if (lh < target * 0.7)
                ins.Add(new Insight("lane", -2, $"Линия: {lh} добиваний к 10:00 при ориентире {target} для роли",
                    $"Lane: {lh} last hits by 10:00, role target {target}"));
            else if (lh >= target)
                ins.Add(new Insight("lane", 1, $"Линия: {lh} добиваний к 10:00 — на уровне ориентира ({target})",
                    $"Lane: {lh} last hits by 10:00 — on target ({target})"));
        }
        if (core && r.LaneEfficiency is double eff && eff < 50)
            ins.Add(new Insight("lane", -1, $"Эффективность линии {eff:0}% — много золота с линии потеряно",
                $"Lane efficiency {eff:0}% — a lot of lane gold lost"));

        // 3. Тайминги ключевых предметов
        foreach (var it in r.Items)
        {
            if (it.Delta is not int delta) continue;
            var (ru, en) = itemName(it.ItemKey);
            if (delta >= 150)
                ins.Add(new Insight("item", delta >= 300 ? -2 : -1,
                    $"{ru} на {Clock(delta)} позже нормы ({Clock(it.Yours)} против {Clock(it.Norm!.Value)})",
                    $"{en} {Clock(delta)} later than usual ({Clock(it.Yours)} vs {Clock(it.Norm!.Value)})"));
            else if (delta <= -90)
                ins.Add(new Insight("item", 1,
                    $"{ru} раньше нормы на {Clock(-delta)} ({Clock(it.Yours)})",
                    $"{en} {Clock(-delta)} faster than usual ({Clock(it.Yours)})"));
        }

        // 4. Кто убивает
        if (r.Killers.FirstOrDefault() is { Count: >= 3 } top)
        {
            var (ru, en) = heroName(top.HeroId);
            ins.Add(new Insight("deaths", -1, $"Чаще всего вас убивал {ru} — {top.Count} раза", $"{en} killed you the most — {top.Count} times"));
        }

        // 5. Варды (саппорты)
        if (position is Position.SoftSupport or Position.HardSupport && r.Observers.Count >= 3)
        {
            int dewarded = r.Observers.Count(w => w.Dewarded);
            if (r.ObserverAvgLife is double life && life < 200)
                ins.Add(new Insight("wards", -1,
                    $"Варды жили в среднем {Clock((int)life)} из 6:00 — их находят, ставьте глубже или в другие точки",
                    $"Observers lived {Clock((int)life)} of 6:00 on average — they get found, place them elsewhere"));
            if (dewarded * 2 >= r.Observers.Count)
                ins.Add(new Insight("wards", -1, $"Враги сняли {dewarded} из {r.Observers.Count} ваших обсерверов",
                    $"Enemies dewarded {dewarded} of your {r.Observers.Count} observers"));
            else if (r.ObserverAvgLife is double l2 && l2 >= 300)
                ins.Add(new Insight("wards", 1, $"Варды живут почти всё время ({Clock((int)l2)} в среднем)", $"Your observers last almost full time ({Clock((int)l2)} avg)"));
        }
        if (position == Position.SoftSupport || position == Position.HardSupport)
            if (r.Observers.Count < 3 && d.Duration > 1500)
                ins.Add(new Insight("wards", -1, $"Всего {r.Observers.Count} обсервера за {Clock(d.Duration)} — карта была тёмной",
                    $"Only {r.Observers.Count} observers in {Clock(d.Duration)} — the map was dark"));
        return r;
    }

    private static string Raw(Benchmark b) => b.Raw >= 100 ? b.Raw.ToString("0") : b.Raw.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    public static string Clock(int s) => $"{s / 60}:{Math.Abs(s % 60):00}";
}

/// <summary>Разборы матчей на диске (cache\reviews\{matchId}.json).</summary>
public sealed class MatchReviewStore
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly string _dir;
    private readonly Dictionary<string, MatchReview?> _cache = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public MatchReviewStore(string dir) { _dir = dir; }

    public event EventHandler<string>? Saved;

    public MatchReview? Get(string? matchId)
    {
        if (string.IsNullOrEmpty(matchId) || matchId.Any(c => !char.IsDigit(c))) return null;
        lock (_sync)
        {
            if (_cache.TryGetValue(matchId, out var cached)) return cached;
            MatchReview? r = null;
            try
            {
                var path = Path.Combine(_dir, matchId + ".json");
                if (File.Exists(path)) r = JsonSerializer.Deserialize<MatchReview>(File.ReadAllText(path), Json);
            }
            catch (Exception ex) { Log.Warn($"Разбор {matchId} повреждён: {ex.Message}"); }
            _cache[matchId] = r;
            return r;
        }
    }

    public void Save(MatchReview r)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(Path.Combine(_dir, r.MatchId + ".json"), JsonSerializer.Serialize(r, Json));
            }
            catch (Exception ex) { Log.Warn("Не удалось сохранить разбор: " + ex.Message); }
            _cache[r.MatchId] = r;
        }
        Saved?.Invoke(this, r.MatchId);
    }

    /// <summary>Последние разборы (по времени создания).</summary>
    public IReadOnlyList<MatchReview> Recent(int count)
    {
        try
        {
            if (!Directory.Exists(_dir)) return Array.Empty<MatchReview>();
            return Directory.GetFiles(_dir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(count)
                .Select(f => Get(Path.GetFileNameWithoutExtension(f))).Where(r => r != null).Select(r => r!).ToList();
        }
        catch { return Array.Empty<MatchReview>(); }
    }

    /// <summary>Что проседает чаще всего за последние разборы: средний процентиль по каждому показателю (минимум 3 матча).</summary>
    public static (string Key, double AvgPct, int Matches)? Weakest(IEnumerable<MatchReview> reviews)
    {
        var groups = reviews.SelectMany(r => r.Benchmarks).GroupBy(b => b.Key)
            .Select(g => (Key: g.Key, Avg: g.Average(b => b.Pct), N: g.Count())).Where(g => g.N >= 3).OrderBy(g => g.Avg).ToList();
        return groups.Count > 0 && groups[0].Avg < 0.45 ? (groups[0].Key, groups[0].Avg, groups[0].N) : null;
    }
}

/// <summary>Закономерности между матчами (по вашей локальной истории с известным результатом).</summary>
public static class Patterns
{
    public sealed record Bucket(string Ru, string En, int Games, int Wins)
    {
        public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
    }

    public static IReadOnlyList<Bucket> ByDuration(IEnumerable<MatchRecord> all)
    {
        var known = all.Where(m => m.Result != MatchResult.Unknown && m.DurationSec > 0).ToList();
        return new[]
        {
            Make("До 30 минут", "Under 30 minutes", known.Where(m => m.DurationSec < 1800)),
            Make("30–45 минут", "30–45 minutes", known.Where(m => m.DurationSec is >= 1800 and < 2700)),
            Make("Дольше 45 минут", "Over 45 minutes", known.Where(m => m.DurationSec >= 2700)),
        };
    }

    public static IReadOnlyList<Bucket> ByTimeOfDay(IEnumerable<MatchRecord> all)
    {
        var known = all.Where(m => m.Result != MatchResult.Unknown && m.StartedUtc > DateTime.MinValue).ToList();
        int H(MatchRecord m) => m.StartedUtc.ToLocalTime().Hour;
        return new[]
        {
            Make("Днём (6–18)", "Daytime (6–18)", known.Where(m => H(m) is >= 6 and < 18)),
            Make("Вечером (18–24)", "Evening (18–24)", known.Where(m => H(m) >= 18)),
            Make("Ночью (0–6)", "Night (0–6)", known.Where(m => H(m) < 6)),
        };
    }

    /// <summary>Игры сразу после двух поражений подряд против всех остальных.</summary>
    public static (Bucket AfterLosses, Bucket Others) AfterLossStreak(IEnumerable<MatchRecord> all)
    {
        var known = all.Where(m => m.Result != MatchResult.Unknown).OrderBy(m => m.StartedUtc).ToList();
        var after = new List<MatchRecord>();
        var others = new List<MatchRecord>();
        for (int i = 0; i < known.Count; i++)
        {
            bool streak = i >= 2 && known[i - 1].Result == MatchResult.Loss && known[i - 2].Result == MatchResult.Loss
                          && known[i].StartedUtc - known[i - 1].StartedUtc < TimeSpan.FromHours(3);
            (streak ? after : others).Add(known[i]);
        }
        return (Make("После 2 поражений подряд", "After 2 losses in a row", after), Make("Остальные игры", "Other games", others));
    }

    /// <summary>Выводы: заметная разница (≥ 12 п.п.) при достаточном числе игр (≥ 6 в каждой группе).</summary>
    public static IReadOnlyList<Insight> Insights(IEnumerable<MatchRecord> all)
    {
        var list = all.ToList();
        var result = new List<Insight>();
        int total = list.Count(m => m.Result != MatchResult.Unknown);
        if (total < 10) return result;
        double overall = (double)list.Count(m => m.Result == MatchResult.Win) / total;

        foreach (var b in ByDuration(list).Concat(ByTimeOfDay(list)))
        {
            if (b.Games < 6) continue;
            double diff = b.WinRate - overall;
            if (diff <= -0.12)
                result.Add(new Insight("pattern", -2, $"{b.Ru}: {b.WinRate:P0} побед против {overall:P0} в среднем ({b.Games} игр)",
                    $"{b.En}: {b.WinRate:P0} wins vs {overall:P0} overall ({b.Games} games)"));
            else if (diff >= 0.12)
                result.Add(new Insight("pattern", 1, $"{b.Ru}: {b.WinRate:P0} побед — ваше сильное время ({b.Games} игр)",
                    $"{b.En}: {b.WinRate:P0} wins — your strong spot ({b.Games} games)"));
        }
        var (afterLoss, rest) = AfterLossStreak(list);
        if (afterLoss.Games >= 6 && afterLoss.WinRate <= rest.WinRate - 0.12)
            result.Add(new Insight("pattern", -2,
                $"После двух поражений подряд вы выигрываете {afterLoss.WinRate:P0} ({afterLoss.Games} игр) против {rest.WinRate:P0} обычно — сделайте перерыв",
                $"After two losses in a row you win {afterLoss.WinRate:P0} ({afterLoss.Games} games) vs {rest.WinRate:P0} usually — take a break"));
        return result.OrderBy(i => i.Severity).ToList();
    }

    private static Bucket Make(string ru, string en, IEnumerable<MatchRecord> games)
    {
        var g = games.ToList();
        return new Bucket(ru, en, g.Count, g.Count(m => m.Result == MatchResult.Win));
    }
}
