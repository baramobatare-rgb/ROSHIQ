using Roshiq.Core.Models;
using Roshiq.Data;

namespace Roshiq.Recommendations;

public sealed record ItemPick(Item Item, double Score, IReadOnlyList<Reason> Reasons, bool Curated)
{
    public string Explain(Language lang) =>
        Item.Purpose(lang) + (Reasons.Count > 0 ? ". " + string.Join("; ", Reasons.Select(r => r.Text(lang))) : "");
}

public sealed record ItemAdaptation(ItemPick Pick, Item? Replace, bool AlreadyIncluded);

public sealed class BuildRecommendation
{
    public required Hero Hero { get; init; }
    public required Position Position { get; init; }
    public required IReadOnlyDictionary<BuildPhase, IReadOnlyList<ItemPick>> Phases { get; init; }
    /// <summary>Учтены ли герои противника (только в тренировочном контексте).</summary>
    public required bool EnemyAware { get; init; }
    public required bool HasCuratedBase { get; init; }

    public IReadOnlyList<ItemPick> this[BuildPhase phase] =>
        Phases.TryGetValue(phase, out var list) ? list : Array.Empty<ItemPick>();

    /// <summary>Следующий рекомендуемый предмет, которого ещё нет у игрока (для карточки Minimal).</summary>
    public ItemPick? NextItem(IReadOnlyCollection<string>? owned, int? gameClockSec = null)
    {
        var order = new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late };
        IEnumerable<BuildPhase> phases = order;
        if (gameClockSec is > 0) phases = order.Skip(1); // стартовые предметы после 0:00 уже не актуальны
        foreach (var phase in phases)
            foreach (var pick in this[phase])
                if (owned == null || !owned.Contains(pick.Item.Key, StringComparer.OrdinalIgnoreCase))
                    return pick;
        return null;
    }
}

/// <summary>
/// Подбор предметов для конкретного героя и роли.
/// Оценка = ценность тегов предмета для позиции + совместимость с признаками героя;
/// предметы, требования которых герой не выполняет (например, Battle Fury для героя дальнего боя), отбрасываются.
/// Для части героев используется проверенная базовая сборка из hero_builds.txt.
/// </summary>
public sealed class ItemBuildEngine
{
    private static readonly Dictionary<BuildPhase, int> PhaseSize = new()
    {
        [BuildPhase.Start] = 4,
        [BuildPhase.Early] = 3,
        [BuildPhase.Mid] = 3,
        [BuildPhase.Late] = 3,
        [BuildPhase.Situational] = 5,
        [BuildPhase.Alternative] = 4
    };

    private readonly GameDatabase _db;

    public ItemBuildEngine(GameDatabase db) { _db = db; }

    public BuildRecommendation Build(Hero hero, Position position, IReadOnlyList<Hero>? enemies = null)
    {
        var enemyList = enemies ?? Array.Empty<Hero>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phases = new Dictionary<BuildPhase, IReadOnlyList<ItemPick>>();
        // базовая сборка — для этой роли, а если её нет, то для ближайшей роли героя из той же группы (кор / саппорт)
        var basePosition = _db.CuratedPosition(hero, position);
        // группы предметов из настоящей сборки (сапоги и т. п.) заняты сразу: догадка на стадии без данных не добавит вторую пару сапог,
        // если настоящие сапоги стоят в более поздней стадии
        if (basePosition is Position reserved)
        {
            foreach (var phase in new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late })
                foreach (var key in _db.CuratedBuild(hero, reserved, phase) ?? Array.Empty<string>())
                    if (_db.FindItem(key) is { Group: { } group }) usedGroups.Add(group);
        }

        foreach (var phase in new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late })
        {
            var picks = new List<ItemPick>();
            var curated = basePosition is Position bp ? _db.CuratedBuild(hero, bp, phase) : null;
            if (curated != null)
            {
                foreach (var key in curated)
                {
                    var item = _db.FindItem(key);
                    if (item == null || used.Contains(item.Key)) continue;
                    var (s, rs) = Score(item, hero, position);
                    var reasons = new List<Reason>(rs)
                    {
                        new(ReasonKind.Item, 0, "часто покупают на этом герое (OpenDota)", "often bought on this hero (OpenDota)")
                    };
                    picks.Add(new ItemPick(item, s, reasons, true));
                    Mark(item, used, usedGroups);
                }
            }

            // есть настоящая сборка на эту стадию — догадки движка к ней не дописываем; подбор по признакам только там, где данных нет
            bool fromData = picks.Count > 0;
            foreach (var pick in RankPhase(phase, hero, position, used, usedGroups))
            {
                if (fromData || picks.Count >= PhaseSize[phase]) break;
                picks.Add(pick);
                Mark(pick.Item, used, usedGroups);
            }
            phases[phase] = picks;
        }

        var situational = Situational(hero, position, enemyList, used);
        phases[BuildPhase.Situational] = situational;
        var excluded = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
        foreach (var p in situational) excluded.Add(p.Item.Key);
        phases[BuildPhase.Alternative] = Alternative(hero, position, excluded);

        return new BuildRecommendation
        {
            Hero = hero,
            Position = position,
            Phases = phases,
            EnemyAware = enemyList.Count > 0,
            HasCuratedBase = basePosition != null
        };
    }

    /// <summary>Up to three distinct counter options, including items already in the baseline.</summary>
    public IReadOnlyList<ItemAdaptation> Adaptations(Hero hero, Position position, IReadOnlyList<Hero> enemies, BuildRecommendation baseline)
    {
        var unique = enemies.DistinctBy(e => e.Id).Take(5).ToList();
        if (unique.Count == 0) return Array.Empty<ItemAdaptation>();
        var core = new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late }
            .SelectMany(p => baseline[p]).Select(p => p.Item).ToList();
        return _db.Items.Where(i => !i.CatalogOnly && i.Counters.Count > 0 && i.SuitsPosition(position) && i.SuitsHero(hero))
            .Select(i =>
            {
                var hits = unique.Where(e => i.Counters.Any(e.Has)).ToList();
                var reasons = hits.Select(e => new Reason(ReasonKind.Counter, 1,
                    $"Против {e.DisplayName(Language.Russian)}: {string.Join(", ", i.Counters.Where(e.Has).Select(t => _db.TraitText(t, Language.Russian)))}",
                    $"Against {e.DisplayName(Language.English)}: {string.Join(", ", i.Counters.Where(e.Has).Select(t => _db.TraitText(t, Language.English)))}")).ToList();
                var (fit, _) = Score(i, hero, position);
                return new ItemPick(i, hits.Count * 3 + fit * 0.2, reasons, false);
            })
            .Where(p => p.Reasons.Count > 0)
            .OrderByDescending(p => p.Score).ThenBy(p => p.Item.Key, StringComparer.Ordinal)
            .GroupBy(p => p.Item.Group ?? p.Item.Key).Select(g => g.First()).Take(3)
            .Select(p => new ItemAdaptation(p,
                p.Item.Group == null ? null : core.LastOrDefault(i => i.Group == p.Item.Group && i.Key != p.Item.Key),
                core.Any(i => i.Key == p.Item.Key))).ToList();
    }

    /// <summary>Оценка предмета для героя и позиции с объяснением вклада.</summary>
    public (double Score, IReadOnlyList<Reason> Reasons) Score(Item item, Hero hero, Position position)
    {
        double score = 0;
        var contributions = new List<(string Tag, double W, bool FromHero, string? Trait)>();
        foreach (var tag in item.Provides)
        {
            double role = _db.RoleWeight(position, tag);
            score += role;
            double heroW = 0;
            string? bestTrait = null;
            foreach (var trait in hero.Traits)
            {
                var a = _db.Affinity(trait, tag);
                if (a > heroW) { heroW = a; bestTrait = trait; }
                score += a;
            }
            if (role > 0 || heroW > 0) contributions.Add((tag, role + heroW, heroW > 0, bestTrait));
        }

        var reasons = contributions
            .OrderByDescending(c => c.W)
            .Take(2)
            .Where(c => c.FromHero && c.Trait != null)
            .Select(c => new Reason(ReasonKind.Item, c.W,
                $"подходит герою: {_db.TraitText(c.Trait!, Language.Russian)}",
                $"fits the hero: {_db.TraitText(c.Trait!, Language.English)}"))
            .GroupBy(r => r.Ru).Select(g => g.First())
            .ToList();
        return (score, reasons);
    }

    private IEnumerable<ItemPick> RankPhase(BuildPhase phase, Hero hero, Position position,
        HashSet<string> used, HashSet<string> usedGroups)
    {
        return _db.Items
            .Where(i => !i.CatalogOnly && i.Phase == phase && i.SuitsPosition(position) && i.SuitsHero(hero) && !used.Contains(i.Key))
            .Where(i => i.Group == null || !usedGroups.Contains(i.Group))
            .Select(i => { var (s, r) = Score(i, hero, position); return new ItemPick(i, s, r, false); })
            .Where(p => p.Score >= 1.0)
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Item.Key, StringComparer.Ordinal)
            .GroupBy(p => p.Item.Group ?? p.Item.Key) // не больше одного предмета из группы
            .Select(g => g.First());
    }

    private IReadOnlyList<ItemPick> Situational(Hero hero, Position position, IReadOnlyList<Hero> enemies, HashSet<string> used)
    {
        var pool = _db.Items
            .Where(i => !i.CatalogOnly && i.Counters.Count > 0 && i.SuitsPosition(position) && i.SuitsHero(hero) && !used.Contains(i.Key)
                        && (i.Phase is BuildPhase.Mid or BuildPhase.Late or BuildPhase.Early))
            .ToList();

        var result = new List<ItemPick>();
        if (enemies.Count > 0)
        {
            foreach (var item in pool)
            {
                var hits = enemies
                    .SelectMany(e => item.Counters.Where(e.Has).Select(t => (Enemy: e, Trait: t)))
                    .ToList();
                if (hits.Count == 0) continue;
                var (baseScore, _) = Score(item, hero, position);
                double s = hits.Count * 1.5 + baseScore * 0.3;
                var byTrait = hits.GroupBy(h => h.Trait).Select(g =>
                    new Reason(ReasonKind.Counter, g.Count(),
                        $"против: {string.Join(", ", g.Select(x => x.Enemy.Name).Distinct())} ({_db.TraitText(g.Key, Language.Russian)})",
                        $"vs {string.Join(", ", g.Select(x => x.Enemy.Name).Distinct())} ({_db.TraitText(g.Key, Language.English)})"))
                    .ToList();
                result.Add(new ItemPick(item, s, byTrait, false));
            }
        }
        else
        {
            foreach (var item in pool)
            {
                var (s, _) = Score(item, hero, position);
                if (s < 0.8) continue;
                var traitsRu = string.Join(", ", item.Counters.Select(t => _db.TraitText(t, Language.Russian)));
                var traitsEn = string.Join(", ", item.Counters.Select(t => _db.TraitText(t, Language.English)));
                result.Add(new ItemPick(item, s, new[]
                {
                    new Reason(ReasonKind.Info, 0, $"если у противника: {traitsRu}", $"if enemies have: {traitsEn}")
                }, false));
            }
        }

        return result
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Item.Key, StringComparer.Ordinal)
            .Take(PhaseSize[BuildPhase.Situational])
            .ToList();
    }

    /// <summary>Альтернатива для поздних стадий: следующие по оценке предметы, не вошедшие в основную сборку.</summary>
    private IReadOnlyList<ItemPick> Alternative(Hero hero, Position position, HashSet<string> used)
    {
        return _db.Items
            .Where(i => !i.CatalogOnly && (i.Phase is BuildPhase.Mid or BuildPhase.Late) && i.SuitsPosition(position) && i.SuitsHero(hero) && !used.Contains(i.Key))
            .Select(i => { var (s, r) = Score(i, hero, position); return new ItemPick(i, s, r, false); })
            .Where(p => p.Score >= 1.0)
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Item.Key, StringComparer.Ordinal)
            .Take(PhaseSize[BuildPhase.Alternative])
            .ToList();
    }

    private static void Mark(Item item, HashSet<string> used, HashSet<string> groups)
    {
        used.Add(item.Key);
        if (item.Group != null) groups.Add(item.Group);
    }
}
