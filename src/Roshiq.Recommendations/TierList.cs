using Roshiq.Core.Models;
using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Группа рангов для тир-листа.</summary>
public enum RankBracket { All, Low, Mid, High, Immortal, Pro }

public sealed record TierEntry(Hero Hero, string Tier, double WinRate, double PickRate, int Games);

/// <summary>
/// 0.23.0: тир-лист по позиции — доля побед героя в выбранных рангах по данным OpenDota (/heroStats, последние ~30 дней пабликов).
/// OpenDota не делит статистику по позициям: герой попадает в позицию по нашей базе героев, а процент — общий по всем его матчам.
/// Редкие герои сглажены к 50% (+100 игр по 50%), чтобы 8 побед из 10 не выглядели «S-тиром».
/// </summary>
public static class TierList
{
    public static readonly string[] Tiers = { "S", "A", "B", "C", "D" };
    /// <summary>Минимальная доля матчей с героем, чтобы попасть в список (паблики): реже 0,5% — данных мало.</summary>
    public const double MinPickRate = 0.005;
    public const int MinProGames = 10;
    private const double Prior = 100;

    public static IReadOnlyList<int> Brackets(RankBracket b) => b switch
    {
        RankBracket.Low => new[] { 0, 1, 2 },
        RankBracket.Mid => new[] { 3, 4 },
        RankBracket.High => new[] { 5, 6 },
        RankBracket.Immortal => new[] { 7 },
        _ => new[] { 0, 1, 2, 3, 4, 5, 6, 7 }
    };

    /// <summary>Группа по медали из профиля (rank_tier 54 → Легенда → Mid). Без медали — все ранги.</summary>
    public static RankBracket FromRankTier(int? rankTier) => (rankTier / 10) switch
    {
        1 or 2 or 3 => RankBracket.Low,
        4 or 5 => RankBracket.Mid,
        6 or 7 => RankBracket.High,
        8 => RankBracket.Immortal,
        _ => RankBracket.All
    };

    public static string TierFor(double winRate) => winRate switch
    {
        >= 0.53 => "S",
        >= 0.515 => "A",
        >= 0.50 => "B",
        >= 0.485 => "C",
        _ => "D"
    };

    public static List<TierEntry> Build(HeroTierStats stats, IEnumerable<Hero> heroes, Position position, RankBracket bracket)
    {
        var byId = stats.Rows.ToDictionary(r => r.HeroId);
        var idx = Brackets(bracket);
        // всего матчей в выбранных рангах: каждый матч — 10 выборов героев
        double totalMatches = bracket == RankBracket.Pro
            ? stats.Rows.Sum(r => r.ProPick) / 10.0
            : stats.Rows.Sum(r => idx.Sum(i => r.Picks[i])) / 10.0;
        if (totalMatches <= 0) return new List<TierEntry>();

        var list = new List<TierEntry>();
        foreach (var h in heroes.Where(h => h.PlaysPosition(position)))
        {
            if (!byId.TryGetValue(h.Id, out var r)) continue;
            int games = bracket == RankBracket.Pro ? r.ProPick : idx.Sum(i => r.Picks[i]);
            int wins = bracket == RankBracket.Pro ? r.ProWin : idx.Sum(i => r.Wins[i]);
            double pick = games / totalMatches;
            if (bracket == RankBracket.Pro ? games < MinProGames : pick < MinPickRate) continue;
            double wr = (wins + Prior * 0.5) / (games + Prior);
            list.Add(new TierEntry(h, TierFor(wr), wr, pick, games));
        }
        return list.OrderByDescending(e => e.WinRate).ToList();
    }
}
