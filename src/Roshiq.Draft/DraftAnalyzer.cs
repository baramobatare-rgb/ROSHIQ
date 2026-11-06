using Roshiq.Core.Models;
using Roshiq.Data;

namespace Roshiq.Draft;

/// <summary>Видимое игроку состояние драфта.</summary>
public sealed record DraftState(
    IReadOnlyList<Hero> Allies,
    IReadOnlyList<Hero> Enemies,
    Position UserPosition,
    IReadOnlyCollection<int>? Unavailable = null)
{
    public static DraftState Empty(Position p) => new(Array.Empty<Hero>(), Array.Empty<Hero>(), p);
}

public sealed record HeroRecommendation(
    Hero Hero,
    double Score,
    Confidence Confidence,
    IReadOnlyList<Reason> Reasons,
    IReadOnlyList<MatchupStat> Stats)
{
    public IEnumerable<Reason> Positives => Reasons.Where(r => r.Kind != ReasonKind.Risk);
    public IEnumerable<Reason> Risks => Reasons.Where(r => r.Kind == ReasonKind.Risk);
}

public sealed record DraftAnalysis(
    IReadOnlyList<HeroRecommendation> Top,
    IReadOnlyList<HeroRecommendation> Alternatives,
    bool HasEnoughData,
    IReadOnlyList<Reason> Notes);

/// <summary>
/// Локальный анализ выбора героев: правила контрпиков, синергии, пробелы в составе,
/// соответствие роли и (если в кэше есть достоверные данные) реальная статистика матчапов.
/// Проценты побед не генерируются: используются только данные из внешнего источника с указанием числа игр.
/// </summary>
public sealed class DraftAnalyzer
{
    private readonly GameDatabase _db;
    private readonly IMatchupStatsProvider _stats;
    private readonly int _minGames;

    public DraftAnalyzer(GameDatabase db, IMatchupStatsProvider? stats = null, int minGamesForStats = 200)
    {
        _db = db;
        _stats = stats ?? NoStatsProvider.Instance;
        _minGames = Math.Max(1, minGamesForStats);
    }

    /// <summary>Личный пул героев (id). Пустой — пул не учитывается.</summary>
    public IReadOnlySet<int> Pool { get; set; } = new HashSet<int>();
    public PoolMode PoolMode { get; set; } = PoolMode.Off;

    private bool PoolActive => PoolMode != PoolMode.Off && Pool.Count > 0;

    /// <summary>Ваша статистика на героях (heroId → игры, победы) из привязанного Steam-аккаунта.</summary>
    public IReadOnlyDictionary<int, (int Games, int Wins)> Personal { get; set; } = new Dictionary<int, (int, int)>();

    /// <summary>Минимум игр на герое, чтобы личная статистика что-то значила.</summary>
    public const int PersonalMinGames = 8;

    /// <summary>Ваш винрейт против героев противника (heroId → игры против, победы) из привязанного Steam.</summary>
    public IReadOnlyDictionary<int, (int Games, int Wins)> PersonalAgainst { get; set; } = new Dictionary<int, (int, int)>();

    /// <summary>Предупреждения «против этого героя вы часто проигрываете» (≥ 12 игр и меньше 43% побед).</summary>
    public IReadOnlyList<Reason> AgainstWarnings(IEnumerable<Hero> enemies)
    {
        var list = new List<Reason>();
        foreach (var e in enemies)
            if (PersonalAgainst.TryGetValue(e.Id, out var a) && a.Games >= 12 && (double)a.Wins / a.Games < 0.43)
            {
                double wr = (double)a.Wins / a.Games;
                list.Add(new Reason(ReasonKind.Risk, 0,
                    $"Против {e.NameRu} у вас {wr:P0} побед за {a.Games} игр — берите героя, который его контрит, или играйте от линии осторожнее",
                    $"You win {wr:P0} of {a.Games} games against {e.Name} — pick a counter or play the lane safer"));
            }
        return list;
    }

    public DraftAnalysis Analyze(DraftState state, int top = 3, int alternatives = 7, bool countersOnly = false)
    {
        var taken = new HashSet<int>(state.Allies.Select(h => h.Id).Concat(state.Enemies.Select(h => h.Id)));
        if (state.Unavailable != null) taken.UnionWith(state.Unavailable);

        var candidates = _db.Heroes
            .Where(h => !taken.Contains(h.Id) && h.PlaysPosition(state.UserPosition))
            .Where(h => !(PoolActive && PoolMode == PoolMode.Only) || Pool.Contains(h.Id))
            .Select(h => Evaluate(h, state))
            .Where(r => !countersOnly || r.Reasons.Any(reason => reason.Weight > 0 && reason.Kind is ReasonKind.Counter or ReasonKind.Stats))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Hero.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var notes = new List<Reason>();
        bool enough = state.Enemies.Count > 0 || state.Allies.Count > 0;
        if (!enough)
            notes.Add(new Reason(ReasonKind.Info, 0,
                "Пока нет видимых героев — список основан только на роли. Уверенность низкая.",
                "No heroes visible yet — the list is based on role only. Low confidence."));
        if (PoolActive && PoolMode == PoolMode.Only)
            notes.Add(new Reason(ReasonKind.Info, 0,
                "Показаны только герои из вашего пула (избранные).",
                "Only heroes from your pool (favorites) are shown."));
        notes.AddRange(AgainstWarnings(state.Enemies));
        if (_stats is NoStatsProvider)
            notes.Add(new Reason(ReasonKind.Info, 0,
                "Статистика матчапов не загружена: рекомендации основаны на взаимодействии способностей.",
                "Matchup statistics not loaded: recommendations rely on ability interactions."));

        return new DraftAnalysis(
            candidates.Take(top).ToList(),
            candidates.Skip(top).Take(alternatives).ToList(),
            enough,
            notes);
    }

    /// <summary>Оценка одного героя (публично — для экрана "почему этот герой").</summary>
    public HeroRecommendation Evaluate(Hero candidate, DraftState state)
    {
        var reasons = new List<Reason>();
        double score = 0;

        // 1. Соответствие роли
        int idx = IndexOf(candidate.Positions, state.UserPosition);
        if (idx >= 0)
        {
            double w = idx == 0 ? 1.0 : idx == 1 ? 0.6 : 0.3;
            score += w;
            reasons.Add(new Reason(ReasonKind.RoleFit, w,
                idx == 0 ? "Основная позиция героя" : "Герой может играть на этой позиции",
                idx == 0 ? "Hero's primary position" : "Hero can play this position"));
        }

        int positiveSignals = 0;

        // 1a. Личный пул: при равных условиях — герои, на которых вы играете
        if (PoolActive && Pool.Contains(candidate.Id))
        {
            const double w = 0.75;
            score += w;
            reasons.Add(new Reason(ReasonKind.Info, w, "Герой из вашего пула (избранные)", "Hero from your pool (favorites)"));
        }

        // 1b. Личный опыт на герое (привязанный Steam): уверенный герой — небольшой плюс при равных
        if (Personal.TryGetValue(candidate.Id, out var mine) && mine.Games >= PersonalMinGames)
        {
            double wr = (double)mine.Wins / mine.Games;
            if (wr >= 0.5)
            {
                double w = wr >= 0.56 ? 0.6 : 0.35;
                score += w;
                reasons.Add(new Reason(ReasonKind.Info, w,
                    $"Ваш герой: {mine.Games} игр, {wr:P0} побед", $"Your hero: {mine.Games} games, {wr:P0} wins"));
            }
            else if (wr < 0.42 && mine.Games >= 15)
                reasons.Add(new Reason(ReasonKind.Risk, 0,
                    $"У вас на нём {wr:P0} побед в {mine.Games} играх", $"You win {wr:P0} of {mine.Games} games on it"));
        }

        // 2. Контрпики: (враг имеет A) и (кандидат имеет B)
        foreach (var rule in _db.CounterRules)
        {
            if (!candidate.Has(rule.CandidateTrait)) continue;
            var targets = state.Enemies.Where(e => e.Has(rule.SourceTrait)).ToList();
            if (targets.Count == 0) continue;
            double w = rule.Weight * (1 + 0.5 * (targets.Count - 1));
            score += w;
            positiveSignals++;
            reasons.Add(MakeReason(ReasonKind.Counter, w, rule.Ru, rule.En, "{e}", targets));
        }

        // 3. Риски: враг контрит кандидата по тем же правилам в обратную сторону
        foreach (var rule in _db.CounterRules)
        {
            if (!candidate.Has(rule.SourceTrait)) continue;
            var threats = state.Enemies.Where(e => e.Has(rule.CandidateTrait)).ToList();
            if (threats.Count == 0) continue;
            double w = rule.Weight * 0.8 * (1 + 0.5 * (threats.Count - 1));
            score -= w;
            var traitRu = _db.TraitText(rule.CandidateTrait, Language.Russian);
            var traitEn = _db.TraitText(rule.CandidateTrait, Language.English);
            reasons.Add(new Reason(ReasonKind.Risk, -w,
                $"Риск: у {Names(threats, Language.Russian)} есть {traitRu}, что опасно для этого героя",
                $"Risk: {Names(threats, Language.English)} bring {traitEn}, which threatens this hero"));
        }

        // 4. Синергия с союзниками
        foreach (var rule in _db.SynergyRules)
        {
            if (!candidate.Has(rule.CandidateTrait)) continue;
            var partners = state.Allies.Where(a => a.Has(rule.SourceTrait)).ToList();
            if (partners.Count == 0) continue;
            double w = rule.Weight * (1 + 0.3 * (partners.Count - 1));
            score += w;
            positiveSignals++;
            reasons.Add(MakeReason(ReasonKind.Synergy, w, rule.Ru, rule.En, "{a}", partners));
        }

        // 5. Пробелы в составе
        if (state.Allies.Count > 0)
        {
            foreach (var gap in _db.GapRules)
            {
                if (!candidate.Has(gap.Trait) || state.Allies.Any(a => a.Has(gap.Trait))) continue;
                score += gap.Weight;
                positiveSignals++;
                reasons.Add(new Reason(ReasonKind.TeamGap, gap.Weight, gap.Ru, gap.En));
            }
        }

        // 6. Реальная статистика (только если в кэше достаточно игр)
        var stats = new List<MatchupStat>();
        int statAgree = 0, statDisagree = 0;
        foreach (var enemy in state.Enemies)
        {
            var s = _stats.Get(candidate.Id, enemy.Id);
            if (s == null || s.Games < _minGames) continue;
            stats.Add(s);
            double adv = s.WinRate - 0.5;
            double w = Math.Clamp(adv * 10, -1.0, 1.0);
            score += w;
            if (adv > 0.01) statAgree++;
            else if (adv < -0.01) statDisagree++;
            reasons.Add(new Reason(adv >= 0 ? ReasonKind.Stats : ReasonKind.Risk, w,
                $"{s.Source}: против {enemy.Name} {s.WinRate:P1} побед в {s.Games} играх",
                $"{s.Source}: vs {enemy.Name} {s.WinRate:P1} wins over {s.Games} games"));
        }

        var confidence = ComputeConfidence(positiveSignals, statAgree, statDisagree, state);
        return new HeroRecommendation(candidate, Math.Round(score, 3), confidence,
            reasons.OrderByDescending(r => Math.Abs(r.Weight)).ToList(), stats);
    }

    /// <summary>Герои, которые по правилам взаимодействия способностей хорошо играют против target (для справочника).</summary>
    public IReadOnlyList<(Hero Hero, double Score, IReadOnlyList<Reason> Reasons)> CountersOf(Hero target, int top = 6)
    {
        var list = new List<(Hero, double, IReadOnlyList<Reason>)>();
        foreach (var candidate in _db.Heroes)
        {
            if (candidate.Id == target.Id) continue;
            double score = 0;
            var reasons = new List<Reason>();
            foreach (var rule in _db.CounterRules)
            {
                if (!target.Has(rule.SourceTrait) || !candidate.Has(rule.CandidateTrait)) continue;
                score += rule.Weight;
                reasons.Add(MakeReason(ReasonKind.Counter, rule.Weight, rule.Ru, rule.En, "{e}", new[] { target }));
            }
            foreach (var rule in _db.CounterRules)
            {
                if (!candidate.Has(rule.SourceTrait) || !target.Has(rule.CandidateTrait)) continue;
                score -= rule.Weight * 0.8;
            }
            var s = _stats.Get(candidate.Id, target.Id);
            if (s != null && s.Games >= _minGames)
            {
                score += Math.Clamp((s.WinRate - 0.5) * 10, -1, 1);
                reasons.Add(new Reason(ReasonKind.Stats, 0,
                    $"{s.Source}: {s.WinRate:P1} побед в {s.Games} играх",
                    $"{s.Source}: {s.WinRate:P1} wins over {s.Games} games"));
            }
            if (score > 0) list.Add((candidate, Math.Round(score, 3), reasons.OrderByDescending(r => r.Weight).ToList()));
        }
        return list.OrderByDescending(x => x.Item2).ThenBy(x => x.Item1.Name, StringComparer.OrdinalIgnoreCase).Take(top).ToList();
    }

    internal static Confidence ComputeConfidence(int signals, int statAgree, int statDisagree, DraftState state)
    {
        if (state.Enemies.Count == 0 && state.Allies.Count == 0) return Confidence.Low;
        int level = signals >= 3 ? 2 : signals == 2 ? 1 : 0;
        if (statAgree > statDisagree && statAgree > 0) level++;
        if (statDisagree > statAgree) level--;
        if (state.Enemies.Count <= 1 && level > 1) level = 1; // мало информации о противнике
        return (Confidence)Math.Clamp(level, 0, 2);
    }

    private static Reason MakeReason(ReasonKind kind, double w, string ru, string en, string placeholder, IReadOnlyList<Hero> heroes) =>
        new(kind, w, ru.Replace(placeholder, Names(heroes, Language.Russian)), en.Replace(placeholder, Names(heroes, Language.English)));

    private static string Names(IEnumerable<Hero> heroes, Language lang) =>
        string.Join(", ", heroes.Select(h => h.DisplayName(lang)));

    private static int IndexOf(IReadOnlyList<Position> list, Position p)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == p) return i;
        return -1;
    }
}
