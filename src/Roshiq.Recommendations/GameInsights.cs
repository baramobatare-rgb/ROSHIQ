using System.Text.Json;
using System.Text.Json.Serialization;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;
using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Совет на линию против одного соперника: общие советы по его признакам + ваш личный опыт против него.</summary>
public sealed record LaneOpponentAdvice(Hero Hero, IReadOnlyList<LaneTip> Tips, int? GamesAgainst, double? WinRateAgainst)
{
    public string Personal(Language lang) => GamesAgainst is int g && WinRateAgainst is double wr
        ? lang == Language.English ? $"you win {wr:P0} of {g} games against him" : $"против него у вас {wr:P0} побед за {g} игр"
        : "";
}

/// <summary>Советы на линию: только общие правила игры по признакам героев и ваша собственная статистика.</summary>
public static class LaneAdvisor
{
    public const int TipsPerHero = 2;

    public static IReadOnlyList<LaneOpponentAdvice> Advise(IEnumerable<Hero> laneOpponents, IReadOnlyList<LaneTip> rules,
        IReadOnlyDictionary<int, (int Games, int Wins)>? against = null, int minGames = 5)
    {
        var result = new List<LaneOpponentAdvice>();
        foreach (var h in laneOpponents)
        {
            var tips = rules.Where(r => h.Has(r.Trait)).Take(TipsPerHero).ToList();
            int? games = null;
            double? wr = null;
            if (against != null && against.TryGetValue(h.Id, out var a) && a.Games >= minGames)
            {
                games = a.Games;
                wr = (double)a.Wins / a.Games;
            }
            if (tips.Count > 0 || games != null) result.Add(new LaneOpponentAdvice(h, tips, games, wr));
        }
        return result;
    }
}

public enum TeamPhase { Even, EarlyStronger, LateStronger }

/// <summary>Сравнение составов по фазам игры: у кого сила рано, у кого поздно.</summary>
public sealed record TeamTiming(TeamPhase Ours, IReadOnlyList<Hero> OurEarly, IReadOnlyList<Hero> OurLate,
    IReadOnlyList<Hero> TheirEarly, IReadOnlyList<Hero> TheirLate)
{
    /// <summary>Главный план на игру одной строкой.</summary>
    public (string Ru, string En)? Plan => Ours switch
    {
        TeamPhase.EarlyStronger => ("Ваш состав сильнее рано — давите вышки и Рошана до 25–30 минут, не давайте врагу дофармиться.",
            "Your draft is stronger early — take towers and Roshan before 25–30 minutes, don't let the enemy farm up."),
        TeamPhase.LateStronger => ("Ваш состав сильнее поздно — переживите первые 15–20 минут, не отдавайте лишних смертей и фармите.",
            "Your draft is stronger late — survive the first 15–20 minutes, avoid needless deaths and farm."),
        _ => null
    };
}

/// <summary>Пики силы: фазы составов и момент, когда у вас появился ключевой предмет.</summary>
public static class PowerSpikes
{
    /// <summary>Предметы, после которых у героя заметно меняется сила в драке (вне зависимости от цены).</summary>
    public static readonly IReadOnlySet<string> SpikeItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "item_black_king_bar", "item_blink", "item_bfury", "item_radiance", "item_desolator", "item_manta", "item_ultimate_scepter",
        "item_aghanims_shard", "item_echo_sabre", "item_maelstrom", "item_mjollnir", "item_skadi", "item_butterfly", "item_satanic",
        "item_hand_of_midas", "item_shadow_blade", "item_silver_edge", "item_sange_and_yasha", "item_kaya_and_sange", "item_yasha_and_kaya",
        "item_orchid", "item_bloodthorn", "item_force_staff", "item_glimmer_cape", "item_aether_lens", "item_dagon",
        "item_assault", "item_heart", "item_abyssal_blade", "item_basher", "item_monkey_king_bar", "item_daedalus",
        "item_greater_crit", "item_nullifier", "item_refresher", "item_octarine_core", "item_pipe", "item_crimson_guard",
        "item_vanguard", "item_hurricane_pike", "item_harpoon", "item_disperser", "item_gleipnir", "item_overwhelming_blink",
        "item_swift_blink", "item_arcane_blink", "item_wind_waker", "item_witch_blade", "item_parasma", "item_khanda", "item_revenants_brooch"
    };

    public static TeamTiming Compare(IReadOnlyList<Hero> allies, IReadOnlyList<Hero> enemies)
    {
        var oe = allies.Where(h => h.Has("early_game")).ToList();
        var ol = allies.Where(h => h.Has("late_game")).ToList();
        var te = enemies.Where(h => h.Has("early_game")).ToList();
        var tl = enemies.Where(h => h.Has("late_game")).ToList();
        int early = oe.Count - te.Count, late = ol.Count - tl.Count;
        var phase = TeamPhase.Even;
        if (allies.Count >= 3 && enemies.Count >= 3)
        {
            // перевес считаем, только если он заметный и не противоречит сам себе
            if (early - late >= 2) phase = TeamPhase.EarlyStronger;
            else if (late - early >= 2) phase = TeamPhase.LateStronger;
        }
        return new TeamTiming(phase, oe, ol, te, tl);
    }

    /// <summary>Подсказка по времени игры (один раз за матч на каждую): что сейчас выгодно составу.</summary>
    public static (string Key, string Ru, string En)? ClockReminder(TeamTiming t, int clock)
    {
        if (t.Ours == TeamPhase.EarlyStronger && clock is >= 720 and < 780)
            return ("spike_push", "Сейчас ваше время: соберитесь вместе, сносите вышки и берите Рошана, пока враг не дофармился.",
                "This is your window: group up, take towers and Roshan before the enemy farms up.");
        if (t.Ours == TeamPhase.EarlyStronger && clock is >= 1680 and < 1740)
            return ("spike_closing", "Окно силы вашего состава закрывается — не затягивайте, ищите решающую драку или Рошана.",
                "Your draft's power window is closing — don't drag it out, look for a decisive fight or Roshan.");
        if (t.Ours == TeamPhase.LateStronger && clock is >= 600 and < 660)
            return ("spike_survive", "Враг сильнее сейчас — не отдавайте смертей, фармите безопасно, отдавайте вышки, а не героев.",
                "The enemy is stronger now — don't feed, farm safely, give up towers rather than heroes.");
        if (t.Ours == TeamPhase.LateStronger && clock is >= 2100 and < 2160)
            return ("spike_now", "Время работает на вас — ваш состав уже сильнее, можно навязывать драки.",
                "Time is on your side — your draft is stronger now, you can force fights.");
        return null;
    }

    /// <summary>Новый ключевой предмет в инвентаре (первый раз за матч) — пик силы героя.</summary>
    public static IReadOnlyList<string> NewSpikes(IEnumerable<string> ownedNow, ISet<string> alreadyAnnounced, Func<string, int?> cost, IEnumerable<string> buildCore)
    {
        var core = new HashSet<string>(buildCore, StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var key in ownedNow.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool spike = SpikeItems.Contains(key) || (core.Contains(key) && cost(key) is >= 2500);
            if (spike && alreadyAnnounced.Add(key)) list.Add(key);
        }
        return list;
    }
}

/// <summary>Мета-сборка героя по популярности предметов OpenDota (что сейчас реально собирают).</summary>
public sealed record MetaItem(string ItemKey, int Count, double Share);

public sealed class MetaBuild
{
    public int HeroId { get; init; }
    public DateTime FetchedUtc { get; init; }
    public Dictionary<BuildPhase, List<MetaItem>> Phases { get; init; } = new();
    public int TotalItems => Phases.Values.Sum(p => p.Count);
}

public static class MetaBuildBuilder
{
    /// <summary>Расходники и мелочь, которые не показываем в средних и поздних фазах.</summary>
    private static readonly HashSet<string> Consumables = new(StringComparer.OrdinalIgnoreCase)
    {
        "item_tango", "item_flask", "item_clarity", "item_faerie_fire", "item_enchanted_mango", "item_ward_observer", "item_ward_sentry",
        "item_ward_dispenser", "item_tpscroll", "item_smoke_of_deceit", "item_dust", "item_blood_grenade", "item_tome_of_knowledge",
        "item_cheese", "item_aegis", "item_refresher_shard", "item_famango", "item_great_famango", "item_greater_famango"
    };

    /// <summary>
    /// Собрать мету из ответа itemPopularity: фаза → (id предмета → сколько раз купили).
    /// idToKey — соответствие числового id предмета ключу item_xxx; known — предмет есть в базе ROSHIQ.
    /// </summary>
    public static MetaBuild Build(int heroId, IReadOnlyDictionary<BuildPhase, IReadOnlyDictionary<int, int>> raw,
        IReadOnlyDictionary<int, string> idToKey, Func<string, bool> known, int perPhase = 6)
    {
        var phases = new Dictionary<BuildPhase, List<MetaItem>>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var phase in new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late })
        {
            if (!raw.TryGetValue(phase, out var counts) || counts.Count == 0) continue;
            var candidates = counts
                .Where(kv => idToKey.TryGetValue(kv.Key, out var k) && known(k))
                .Select(kv => (Key: idToKey[kv.Key], Count: kv.Value))
                .Where(x => phase == BuildPhase.Start || !Consumables.Contains(x.Key))
                .ToList();
            int max = candidates.Count == 0 ? 0 : candidates.Max(x => x.Count);
            var items = candidates
                .Where(x => phase == BuildPhase.Start || !used.Contains(x.Key))   // предмет — в первой фазе, где его покупают
                .OrderByDescending(x => x.Count)
                .Take(perPhase)
                .Select(x => new MetaItem(x.Key, x.Count, max == 0 ? 0 : (double)x.Count / max))
                .ToList();
            foreach (var i in items) used.Add(i.ItemKey);
            if (items.Count > 0) phases[phase] = items;
        }
        return new MetaBuild { HeroId = heroId, FetchedUtc = DateTime.UtcNow, Phases = phases };
    }
}

/// <summary>Кэш мета-сборок (cache\\meta_builds.json), обновляется не чаще раза в 3 дня на героя.</summary>
public sealed class MetaBuildCache
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<int, MetaBuild> _builds = new();

    public MetaBuildCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path)) _builds = JsonSerializer.Deserialize<Dictionary<int, MetaBuild>>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception ex) { Log.Warn("Кэш мета-сборок повреждён: " + ex.Message); }
    }

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    public MetaBuild? Get(int heroId) { lock (_sync) return _builds.TryGetValue(heroId, out var b) ? b : null; }

    public bool IsStale(int heroId) => Get(heroId) is not { } b || DateTime.UtcNow - b.FetchedUtc > MaxAge;

    public void Set(MetaBuild build)
    {
        lock (_sync)
        {
            _builds[build.HeroId] = build;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_builds, Json));
            }
            catch (Exception ex) { Log.Warn("Не удалось сохранить мета-сборки: " + ex.Message); }
        }
    }

    /// <summary>Фаза OpenDota (start_game_items…) → фаза сборки ROSHIQ.</summary>
    public static BuildPhase? PhaseOf(string openDotaKey) => openDotaKey switch
    {
        "start_game_items" => BuildPhase.Start,
        "early_game_items" => BuildPhase.Early,
        "mid_game_items" => BuildPhase.Mid,
        "late_game_items" => BuildPhase.Late,
        _ => null
    };
}
