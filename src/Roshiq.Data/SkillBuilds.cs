using System.Text.Json;
using System.Text.RegularExpressions;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Data;

/// <summary>Одно повышение в порядке прокачки: ключ способности, название и талант ли это.</summary>
public sealed record SkillStep(string Key, string Name, bool Talent);

/// <summary>Выбор таланта на уровне 10/15/20/25: сколько про-игроков из Of взяли этот вариант.</summary>
public sealed record TalentPick(int Level, string Name, int Count, int Of);

/// <summary>
/// 0.21.4: порядок прокачки навыков и талантов из недавних про-матчей на герое (OpenDota: /heroes/{id}/matches → /matches/{id}).
/// Order — реальная последовательность одного про-игрока, наиболее похожая на остальные (медоид), а не «средняя» выдуманная.
/// </summary>
public sealed class SkillBuild
{
    public int HeroId { get; init; }
    public DateTime FetchedUtc { get; init; }
    public int Matches { get; init; }
    public List<SkillStep> Order { get; init; } = new();
    public List<TalentPick> Talents { get; init; } = new();

    /// <summary>Что качать на уровне level (1…16 — на этих уровнях очко навыка есть всегда). null — данных нет.</summary>
    public SkillStep? AtLevel(int level) =>
        level >= 1 && level <= SkillBuildBuilder.ReliableLevels && level <= Order.Count ? Order[level - 1] : null;
}

public static class SkillBuildBuilder
{
    /// <summary>До 16-го уровня каждый уровень даёт очко навыка — номер уровня совпадает с номером повышения.</summary>
    public const int ReliableLevels = 16;
    public const int MinMatches = 3;
    public const string AttributesKey = "special_bonus_attributes";

    public static bool IsTalent(string key) => key.StartsWith("special_bonus", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Название из constants/abilities: у талантов вместо чисел шаблоны «+{s:bonus}% Evasion» — убираем их и оставляем суть.
    /// </summary>
    public static string CleanName(string key, string? dname)
    {
        if (key == AttributesKey) return "Attributes";
        if (string.IsNullOrWhiteSpace(dname)) return key;
        var s = Regex.Replace(dname, @"\{s:[^}]*\}", "");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        s = s.TrimStart('+', '-', '%', 'x', ' ').Trim();
        return s.Length == 0 ? key : s;
    }

    /// <param name="sequences">Порядок прокачки по матчам (ключи способностей).</param>
    /// <param name="talentTiers">Таланты героя из constants/hero_abilities: ключ → ярус 1…4 (уровни 10/15/20/25).</param>
    /// <param name="name">Название способности по ключу.</param>
    public static SkillBuild? Build(int heroId, IReadOnlyList<IReadOnlyList<string>> sequences,
        IReadOnlyDictionary<string, int> talentTiers, Func<string, string> name, DateTime nowUtc)
    {
        var seqs = sequences.Where(s => s.Count >= 10).ToList();
        if (seqs.Count < MinMatches) return null;

        // медоид: последовательность, у которой больше всего совпадений по позициям с остальными на первых уровнях
        int window = ReliableLevels;
        var best = seqs.OrderByDescending(s => seqs.Where(o => !ReferenceEquals(o, s))
            .Sum(o => Enumerable.Range(0, Math.Min(window, Math.Min(s.Count, o.Count))).Count(i => s[i] == o[i])))
            .ThenByDescending(s => s.Count)
            .First();
        var order = best.Take(window).Select(k => new SkillStep(k, name(k), IsTalent(k))).ToList();

        var talents = new List<TalentPick>();
        foreach (var tier in talentTiers.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
        {
            var options = tier.Select(kv => kv.Key).ToList();
            var counts = options.Select(o => (Key: o, Count: seqs.Count(s => s.Contains(o)))).ToList();
            int of = seqs.Count(s => options.Any(s.Contains));
            if (of == 0) continue;
            var top = counts.OrderByDescending(c => c.Count).First();
            talents.Add(new TalentPick(5 + tier.Key * 5, name(top.Key), top.Count, of));
        }
        return new SkillBuild { HeroId = heroId, FetchedUtc = nowUtc, Matches = seqs.Count, Order = order, Talents = talents };
    }
}

/// <summary>Кэш порядка прокачки (cache\skill_builds.json): про-матчи меняются медленно — раз в неделю на героя.</summary>
public sealed class SkillBuildCache
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<int, SkillBuild> _data = new();

    public SkillBuildCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                _data = JsonSerializer.Deserialize<Dictionary<int, SkillBuild>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) { Log.Warn("Кэш прокачки повреждён и будет проигнорирован: " + ex.Message); }
    }

    public SkillBuild? Get(int heroId) { lock (_sync) return _data.TryGetValue(heroId, out var b) ? b : null; }

    public bool IsFresh(int heroId, DateTime nowUtc) => Get(heroId) is { } b && nowUtc - b.FetchedUtc < FreshFor;

    public void Put(SkillBuild b)
    {
        string json;
        lock (_sync) { _data[b.HeroId] = b; json = JsonSerializer.Serialize(_data); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) { Log.Warn("Не удалось сохранить кэш прокачки: " + ex.Message); }
    }
}
