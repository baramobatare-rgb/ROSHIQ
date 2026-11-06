using Roshiq.Core.Models;
using Roshiq.Data;

namespace Roshiq.Draft;

public enum TrainerVerdict { Excellent, Good, Weak, OffRole }

/// <summary>Оценка ответа в тренажёре: место выбранного героя среди всех вариантов и разбор причин.</summary>
public sealed record TrainerGrade(
    Hero Pick,
    TrainerVerdict Verdict,
    int Rank,
    int Total,
    double Score,
    double BestScore,
    HeroRecommendation PickEvaluation,
    IReadOnlyList<HeroRecommendation> Best);

/// <summary>
/// Тренажёр драфта вне матча: случайный видимый драфт противника (и часть союзников),
/// игрок выбирает героя на свою позицию, анализатор объясняет, насколько выбор удачен.
/// К игре не подключается и ничего не читает с экрана.
/// </summary>
public sealed class DraftTrainer
{
    private readonly GameDatabase _db;
    private readonly DraftAnalyzer _analyzer;

    public DraftTrainer(GameDatabase db, DraftAnalyzer analyzer)
    {
        _db = db;
        _analyzer = analyzer;
    }

    /// <summary>Новый сценарий: 3–5 героев противника и 0–3 союзника, все разные.</summary>
    public DraftState NewScenario(Random random, Position position)
    {
        var pool = _db.Heroes.OrderBy(_ => random.Next()).ToList();
        int enemies = random.Next(3, 6);
        int allies = random.Next(0, 4);
        var enemyList = pool.Take(enemies).ToList();
        // союзники — на другие позиции, чтобы сценарий был правдоподобным
        var allyList = pool.Skip(enemies).Where(h => !h.PlaysPosition(position) || h.Positions.Count > 1).Take(allies).ToList();
        return new DraftState(allyList, enemyList, position);
    }

    public TrainerGrade Grade(DraftState state, Hero pick)
    {
        var all = _analyzer.Analyze(state with { Unavailable = null }, top: 500, alternatives: 0);
        var ranked = all.Top;
        var evaluation = _analyzer.Evaluate(pick, state);
        int index = -1;
        for (int i = 0; i < ranked.Count; i++)
            if (ranked[i].Hero.Id == pick.Id) { index = i; break; }

        double best = ranked.Count > 0 ? ranked[0].Score : evaluation.Score;
        TrainerVerdict verdict;
        int rank;
        if (index < 0)
        {
            // герой не играет на этой позиции или уже выбран — место считаем по очкам среди всех вариантов
            rank = ranked.Count(r => r.Score > evaluation.Score) + 1;
            verdict = pick.PlaysPosition(state.UserPosition) ? TrainerVerdict.Weak : TrainerVerdict.OffRole;
        }
        else
        {
            rank = index + 1;
            verdict = rank <= 3 || evaluation.Score >= best - 0.25 ? TrainerVerdict.Excellent
                : rank <= Math.Max(10, ranked.Count / 8) ? TrainerVerdict.Good
                : TrainerVerdict.Weak;
        }
        return new TrainerGrade(pick, verdict, rank, Math.Max(ranked.Count, rank), evaluation.Score, best, evaluation, ranked.Take(3).ToList());
    }
}
