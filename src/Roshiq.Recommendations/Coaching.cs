using System.Text.Json;
using System.Text.Json.Serialization;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;
using Roshiq.Data;

namespace Roshiq.Recommendations;

// =====================================================================================
// Личный темп: сравнение текущего матча с вашими же прошлыми матчами
// =====================================================================================

/// <summary>Сравнение на контрольной точке: сейчас против вашего обычного значения.</summary>
public sealed record PaceCheck(int Clock, int Current, int Usual, int Matches, bool SameHero)
{
    public int Delta => Current - Usual;
}

/// <summary>Предмет куплен раньше или позже вашего среднего.</summary>
public sealed record ItemPace(string ItemKey, int At, ItemBenchmark Bench)
{
    public int Delta => At - Bench.AvgSec;
}

/// <summary>
/// Ваш обычный темп по истории: добивания к 10/15/20 минуте и тайминги предметов. Сначала — матчи на этом герое,
/// если их меньше трёх — на этой роли. Ничего не выдумывает: нет истории — нет сравнения.
/// </summary>
public sealed class PaceAdvisor
{
    public const int MinMatches = 3;
    public static readonly int[] Checkpoints = { 600, 900, 1200 };

    private readonly List<MatchRecord> _hero;
    private readonly List<MatchRecord> _role;
    private readonly List<MatchRecord> _all;
    private readonly HashSet<int> _announced = new();
    private readonly HashSet<string> _itemsAnnounced = new(StringComparer.OrdinalIgnoreCase);

    public PaceAdvisor(IEnumerable<MatchRecord> history, int heroId, Position position, string? excludeId = null)
    {
        _all = history.Where(m => m.Id != excludeId).ToList();
        var gsi = _all.Where(m => m.FromGsi && m.Samples.Count > 0).ToList();
        _hero = gsi.Where(m => m.HeroId == heroId).ToList();
        _role = gsi.Where(m => m.Position == position).ToList();
        HeroId = heroId;
        Position = position;
    }

    public int HeroId { get; }
    public Position Position { get; }

    public (int Avg, int Matches, bool SameHero)? UsualLastHits(int clock)
    {
        static List<int> Values(IEnumerable<MatchRecord> src, int c) =>
            src.Select(m => m.LastHitsAt(c)).Where(v => v != null).Select(v => v!.Value).ToList();
        var h = Values(_hero, clock);
        if (h.Count >= MinMatches) return ((int)Math.Round(h.Average()), h.Count, true);
        var r = Values(_role, clock);
        if (r.Count >= MinMatches) return ((int)Math.Round(r.Average()), r.Count, false);
        return null;
    }

    /// <summary>Контрольная точка (10:00, 15:00, 20:00), о которой пора сообщить; каждая — один раз.</summary>
    public PaceCheck? CheckpointDue(int clock, int? lastHits)
    {
        if (lastHits is not int lh) return null;
        foreach (var cp in Checkpoints)
        {
            if (clock < cp || clock >= cp + 20 || !_announced.Add(cp)) continue;
            var usual = UsualLastHits(cp);
            return usual == null ? null : new PaceCheck(cp, lh, usual.Value.Avg, usual.Value.Matches, usual.Value.SameHero);
        }
        return null;
    }

    /// <summary>Сравнить время покупки предмета со средним (нужно минимум 2 прошлых матча). Каждый предмет — один раз.</summary>
    public ItemPace? ItemDue(MatchRecord current, string itemKey, int at)
    {
        if (!_itemsAnnounced.Add(itemKey)) return null;
        var bench = MatchStats.Benchmark(_all, current, itemKey);
        if (bench == null || bench.Matches < 2) return null;
        return new ItemPace(itemKey, at, bench);
    }
}

// =====================================================================================
// Разбор смертей
// =====================================================================================

public sealed record DeathSummary(
    int Total,
    int Early,
    int Mid,
    int Late,
    int WithLotsOfGold,
    int WithoutTeleport,
    int AvgGoldAtRisk,
    int MaxGoldAtRisk,
    int LateWithoutBuyback);

public static class DeathAnalysis
{
    public const int EarlyEnd = 600;
    public const int MidEnd = 1500;
    public const int LotsOfGold = 1000;

    /// <summary>Сводка по смертям матча (только для матчей, записанных по GSI).</summary>
    public static DeathSummary? Analyze(MatchRecord m)
    {
        if (!m.FromGsi) return null;
        var d = m.DeathEvents;
        if (d.Count == 0) return new DeathSummary(0, 0, 0, 0, 0, 0, 0, 0, 0);
        return new DeathSummary(
            d.Count,
            d.Count(x => x.Clock < EarlyEnd),
            d.Count(x => x.Clock >= EarlyEnd && x.Clock < MidEnd),
            d.Count(x => x.Clock >= MidEnd),
            d.Count(x => x.GoldAtRisk >= LotsOfGold),
            d.Count(x => !x.HadTeleport && x.Clock >= 300),
            (int)Math.Round(d.Average(x => x.GoldAtRisk)),
            d.Max(x => x.GoldAtRisk),
            d.Count(x => x.Clock >= 1800 && x.BuybackReady == false));
    }
}

// =====================================================================================
// Цель на следующую игру
// =====================================================================================

public enum GoalKind { GoldOnDeath, FewerDeaths, LastHits10, CarryTeleport, CourierStash, ItemTiming }

public sealed class GoalResult
{
    public string MatchId { get; set; } = "";
    public bool Achieved { get; set; }
    public int Value { get; set; }
    public DateTime Utc { get; set; }
}

/// <summary>Одна конкретная цель, выбранная по самой заметной ошибке прошлого матча.</summary>
public sealed class CoachGoal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public GoalKind Kind { get; set; }
    /// <summary>Порог цели: золото, число смертей, добивания, секунды тайминга.</summary>
    public int Target { get; set; }
    public string? ItemKey { get; set; }
    public string FromMatchId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<GoalResult> Results { get; set; } = new();
    public bool Closed { get; set; }

    [JsonIgnore] public int Achieved => Results.Count(r => r.Achieved);
}

public static class GoalEngine
{
    /// <summary>Сколько раз цель нужно выполнить, чтобы считать её освоенной.</summary>
    public const int AchieveToClose = 2;
    /// <summary>После стольких попыток цель всё равно сменяется, чтобы не застревать.</summary>
    public const int MaxAttempts = 5;

    private static int AlertCount(MatchRecord m, params string[] keys) =>
        keys.Sum(k => m.Alerts.TryGetValue(k, out var c) ? c : 0);

    /// <summary>Проверить цель на матче. null — по этому матчу цель не измерить (нет данных или другая роль).</summary>
    public static (bool Achieved, int Value)? Evaluate(CoachGoal goal, MatchRecord m)
    {
        if (!m.FromGsi) return null;
        switch (goal.Kind)
        {
            case GoalKind.GoldOnDeath:
            {
                int v = m.DeathEvents.Count(d => d.GoldAtRisk >= goal.Target);
                return (v == 0, v);
            }
            case GoalKind.FewerDeaths:
                return (m.Deaths <= goal.Target, m.Deaths);
            case GoalKind.LastHits10:
            {
                if (m.Position is not (Position.Carry or Position.Mid or Position.Offlane)) return null;
                if (m.LastHitsAt(600) is not int lh) return null;
                return (lh >= goal.Target, lh);
            }
            case GoalKind.CarryTeleport:
            {
                int v = m.DeathEvents.Count(d => !d.HadTeleport && d.Clock >= 300) + AlertCount(m, MatchAlerts.NoTeleport);
                return (v == 0, v);
            }
            case GoalKind.CourierStash:
            {
                int v = AlertCount(m, MatchAlerts.ItemsInStash, MatchAlerts.WardsInStash);
                return (v == 0, v);
            }
            case GoalKind.ItemTiming:
            {
                if (goal.ItemKey == null || !m.ItemTimings.TryGetValue(goal.ItemKey, out var at)) return null;
                return (at <= goal.Target + 60, at);
            }
            default:
                return null;
        }
    }

    /// <summary>Выбрать одну самую заметную проблему матча и превратить её в цель. null — явных проблем нет.</summary>
    public static CoachGoal? Suggest(MatchRecord m, IReadOnlyList<MatchRecord> history)
    {
        if (!m.FromGsi) return null;
        var others = history.Where(x => x.Id != m.Id).ToList();
        var candidates = new List<(double Severity, CoachGoal Goal)>();
        CoachGoal G(GoalKind kind, int target, string? item = null) =>
            new() { Kind = kind, Target = target, ItemKey = item, FromMatchId = m.MatchId ?? m.Id };

        int richDeaths = m.DeathEvents.Count(d => d.GoldAtRisk >= DeathAnalysis.LotsOfGold);
        if (richDeaths >= 2) candidates.Add((richDeaths * 2.0, G(GoalKind.GoldOnDeath, DeathAnalysis.LotsOfGold)));

        int noTp = m.DeathEvents.Count(d => !d.HadTeleport && d.Clock >= 300) + AlertCount(m, MatchAlerts.NoTeleport);
        if (noTp >= 2) candidates.Add((noTp * 1.5, G(GoalKind.CarryTeleport, 0)));

        var deathHistory = others.Where(x => x.Position == m.Position).Select(x => x.Deaths).ToList();
        if (deathHistory.Count >= 3)
        {
            double avg = deathHistory.Average();
            if (m.Deaths >= 7 && m.Deaths >= avg + 3)
                candidates.Add((m.Deaths - avg, G(GoalKind.FewerDeaths, Math.Max(1, (int)Math.Round(avg)))));
        }

        if (m.Position is Position.Carry or Position.Mid or Position.Offlane && m.LastHitsAt(600) is int lh10)
        {
            var usual = new PaceAdvisor(others, m.HeroId, m.Position.Value).UsualLastHits(600);
            if (usual != null && lh10 < usual.Value.Avg * 0.85)
                candidates.Add(((usual.Value.Avg - lh10) / 5.0, G(GoalKind.LastHits10, usual.Value.Avg)));
        }

        int stash = AlertCount(m, MatchAlerts.ItemsInStash, MatchAlerts.WardsInStash);
        if (stash >= 3) candidates.Add((stash, G(GoalKind.CourierStash, 0)));

        foreach (var (item, at) in m.ItemTimings)
        {
            var bench = MatchStats.Benchmark(others, m, item);
            if (bench == null || bench.Matches < 2 || at - bench.AvgSec < 180) continue;
            candidates.Add(((at - bench.AvgSec) / 60.0, G(GoalKind.ItemTiming, bench.AvgSec, item)));
        }

        return candidates.Count == 0 ? null : candidates.OrderByDescending(c => c.Severity).First().Goal;
    }
}

/// <summary>Цели и их результаты: %AppData%\Roshiq\goals.json.</summary>
public sealed class GoalStore
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly object _sync = new();

    public GoalStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path)) Goals = JsonSerializer.Deserialize<List<CoachGoal>>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception ex) { Log.Warn("Цели повреждены и будут сброшены: " + ex.Message); }
    }

    public List<CoachGoal> Goals { get; } = new();
    public event EventHandler? Changed;

    public CoachGoal? Active { get { lock (_sync) return Goals.LastOrDefault(g => !g.Closed); } }

    /// <summary>Последние результаты всех целей (новые — первыми).</summary>
    public IReadOnlyList<(CoachGoal Goal, GoalResult Result)> RecentResults(int max = 10)
    {
        lock (_sync)
            return Goals.SelectMany(g => g.Results.Select(r => (g, r))).OrderByDescending(x => x.r.Utc).Take(max).ToList();
    }

    /// <summary>
    /// После сохранения матча: проверить активную цель и, если целей нет, выбрать новую по этому матчу.
    /// Возвращает результат проверки (для уведомления), если он есть.
    /// </summary>
    public (CoachGoal Goal, GoalResult Result)? OnMatchSaved(MatchRecord m, IReadOnlyList<MatchRecord> history)
    {
        if (!m.FromGsi || m.MatchId == null) return null;
        (CoachGoal, GoalResult)? outcome = null;
        lock (_sync)
        {
            var active = Goals.LastOrDefault(g => !g.Closed);
            if (active != null && active.FromMatchId != m.MatchId && active.Results.All(r => r.MatchId != m.MatchId))
            {
                var ev = GoalEngine.Evaluate(active, m);
                if (ev != null)
                {
                    var result = new GoalResult { MatchId = m.MatchId, Achieved = ev.Value.Achieved, Value = ev.Value.Value, Utc = DateTime.UtcNow };
                    active.Results.Add(result);
                    outcome = (active, result);
                    if (active.Achieved >= GoalEngine.AchieveToClose || active.Results.Count >= GoalEngine.MaxAttempts) active.Closed = true;
                }
            }
            if (!Goals.Any(g => !g.Closed))
            {
                var next = GoalEngine.Suggest(m, history);
                if (next != null) Goals.Add(next);
            }
            if (Goals.Count > 60) Goals.RemoveRange(0, Goals.Count - 60);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return outcome;
    }

    public void Skip()
    {
        lock (_sync)
        {
            var active = Goals.LastOrDefault(g => !g.Closed);
            if (active == null) return;
            active.Closed = true;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Goals, Json));
        }
        catch (Exception ex) { Log.Warn("Не удалось сохранить цели: " + ex.Message); }
    }
}

// =====================================================================================
// Тренды
// =====================================================================================

public sealed record TrendSeries(string Key, IReadOnlyList<(DateTime Utc, double Value)> Points, double? Before, double? After)
{
    public double? Change => Before is double b && After is double a && Math.Abs(b) > 0.001 ? (a - b) / b : null;
}

public static class Trends
{
    /// <summary>Последние last матчей по метрике в хронологическом порядке и сравнение старой и новой половины (от 6 точек).</summary>
    public static TrendSeries Build(string key, IEnumerable<MatchRecord> records, Func<MatchRecord, double?> metric, int last = 30)
    {
        var points = records.OrderByDescending(m => m.StartedUtc)
            .Select(m => (m.StartedUtc, Value: metric(m)))
            .Where(x => x.Value != null)
            .Take(last)
            .Select(x => (x.StartedUtc, x.Value!.Value))
            .Reverse()
            .ToList();
        double? before = null, after = null;
        if (points.Count >= 6)
        {
            int half = points.Count / 2;
            before = points.Take(half).Average(p => p.Item2);
            after = points.Skip(points.Count - half).Average(p => p.Item2);
        }
        return new TrendSeries(key, points, before, after);
    }

    /// <summary>Доля побед: последние n матчей с известным итогом против n перед ними.</summary>
    public static (double? Recent, double? Previous) WinRate(IEnumerable<MatchRecord> records, int n = 10)
    {
        var known = records.Where(m => m.Result != MatchResult.Unknown).OrderByDescending(m => m.StartedUtc).ToList();
        double? Rate(IEnumerable<MatchRecord> g)
        {
            var list = g.ToList();
            return list.Count < 3 ? null : (double)list.Count(m => m.Result == MatchResult.Win) / list.Count;
        }
        return (Rate(known.Take(n)), Rate(known.Skip(n).Take(n)));
    }
}

// =====================================================================================
// Сводка после драфта
// =====================================================================================

public sealed record BriefLine(string Ru, string En, IReadOnlyList<Hero> Heroes);

public sealed record Briefing(IReadOnlyList<Hero> LaneOpponents, IReadOnlyList<BriefLine> Lines);

/// <summary>
/// Короткая сводка по видимому драфту противника: вероятные соперники по линии, главные угрозы по признакам героев
/// и у кого сила в начале или в конце игры. Только общедоступные свойства героев.
/// </summary>
public static class BriefingBuilder
{
    private static readonly (string Trait, int MinHeroes, string Ru, string En)[] Rules =
    {
        ("invisibility", 1, "Невидимость — держите пыль и сентри, позже гем.", "Invisibility — carry dust and sentries, gem later."),
        ("illusions", 1, "Иллюзии — пригодится урон по площади.", "Illusions — area damage pays off."),
        ("summons", 1, "Призванные существа — урон по площади и сплэш.", "Summons — area damage and cleave."),
        ("evasion", 1, "Уклонение — понадобится точное попадание (MKB).", "Evasion — you will need true strike (MKB)."),
        ("heal", 2, "Много лечения — снижайте его (Spirit Vessel, Shiva's).", "Lots of healing — reduce it (Spirit Vessel, Shiva's)."),
        ("lifesteal", 1, "Вампиризм — снижение лечения и контроль.", "Lifesteal — heal reduction and control."),
        ("magic_burst", 2, "Сильный магический прокаст — держите дистанцию, BKB / Pipe / Glimmer.", "Heavy magic burst — keep distance, BKB / Pipe / Glimmer."),
        ("physical_burst", 2, "Сильный физический урон — броня, Ghost Scepter, Blade Mail.", "Heavy physical damage — armor, Ghost Scepter, Blade Mail."),
        ("silence", 2, "Много безмолвия — развеивание (Manta, Lotus, Eul's).", "Lots of silences — dispels (Manta, Lotus, Eul's)."),
        ("stun", 3, "Много контроля — BKB, Linken's или Lotus.", "Lots of control — BKB, Linken's or Lotus."),
        ("mobility", 2, "Мобильные герои — нужен надёжный контроль.", "Mobile heroes — you need reliable control."),
        ("passive_reliant", 1, "Зависят от пассивок — Break (Silver Edge).", "Rely on passives — Break (Silver Edge)."),
        ("push", 2, "Сильный пуш — не оставляйте линии и вышки без защиты.", "Strong push — don't leave lanes and towers undefended."),
        ("global", 1, "Глобальное присутствие — враг может появиться в любой драке.", "Global presence — the enemy can join any fight."),
        ("spell_immunity", 1, "Защита от магии — рассчитывайте на физический урон и контроль.", "Spell immunity — rely on physical damage and control."),
    };

    /// <summary>Вероятные позиции соперников по линии для вашей позиции.</summary>
    public static IReadOnlyList<Position> LaneOpponentPositions(Position mine) => mine switch
    {
        Position.Carry or Position.HardSupport => new[] { Position.Offlane, Position.SoftSupport },
        Position.Offlane or Position.SoftSupport => new[] { Position.Carry, Position.HardSupport },
        _ => new[] { Position.Mid }
    };

    public static Briefing Build(IReadOnlyList<Hero> enemies, Position mine, int maxLines = 6)
    {
        var targets = LaneOpponentPositions(mine);
        var lane = enemies.Where(h => h.Positions.Count > 0 && targets.Contains(h.Positions[0])).ToList();
        if (lane.Count == 0) lane = enemies.Where(h => h.Positions.Any(targets.Contains)).Take(targets.Count).ToList();

        var lines = new List<BriefLine>();
        foreach (var (trait, min, ru, en) in Rules)
        {
            var heroes = enemies.Where(h => h.Has(trait)).ToList();
            if (heroes.Count >= min) lines.Add(new BriefLine(ru, en, heroes));
        }
        var early = enemies.Where(h => h.Has("early_game")).ToList();
        if (early.Count > 0)
            lines.Add(new BriefLine("Сильны в начале — до 15 минут играйте аккуратно.", "Strong early — play carefully until 15 minutes.", early));
        var late = enemies.Where(h => h.Has("late_game")).ToList();
        if (late.Count > 0)
            lines.Add(new BriefLine("Сильны в поздней игре — старайтесь закончить раньше.", "Strong late — try to end earlier.", late));

        return new Briefing(lane.Take(targets.Count).ToList(), lines.OrderByDescending(l => l.Heroes.Count).Take(maxLines).ToList());
    }
}
