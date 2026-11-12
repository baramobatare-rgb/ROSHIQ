using Roshiq.Core.Models;

namespace Roshiq.Recommendations;

public enum Lane { Unknown, Top, Mid, Bottom }

/// <summary>Линия по координатам героя из GSI (мировые координаты, центр карты ≈ 0,0; Radiant внизу слева).</summary>
public static class MapLanes
{
    public static Lane Classify(int x, int y)
    {
        // центральная линия — диагональ x ≈ y, без баз в углах
        if (Math.Abs(x - y) < 2200 && Math.Abs(x) < 5000 && Math.Abs(y) < 5000) return Lane.Mid;
        // верхняя линия идёт вдоль левого и верхнего края, нижняя — вдоль нижнего и правого
        if ((x < -4800 || y > 4800) && y - x > 2500) return Lane.Top;
        if ((x > 4800 || y < -4800) && x - y > 2500) return Lane.Bottom;
        return Lane.Unknown;
    }

    /// <summary>Лёгкая линия: у Radiant — нижняя, у Dire — верхняя.</summary>
    public static Lane SafeLane(string team) => team == "dire" ? Lane.Top : Lane.Bottom;
    public static Lane OffLane(string team) => team == "dire" ? Lane.Bottom : Lane.Top;
}

/// <summary>Итог автоопределения роли.</summary>
public sealed record RoleGuess(Position Position, double Confidence, Lane Lane, string ReasonRu, string ReasonEn)
{
    public string Reason(Language lang) => lang == Language.English ? ReasonEn : ReasonRu;
}

/// <summary>
/// Определяет позицию игрока в начале матча: на какой линии стоит герой (0:20–3:30 по GSI),
/// кем этот герой обычно играется, на какой позиции вы сами им играли и сколько добиваний набрано.
/// Ничего не нажимает и не читает память игры — только данные Game State Integration.
/// </summary>
public sealed class LaneRoleDetector
{
    public const int FromSec = 20;
    public const int DecideAtSec = 150;
    public const int GiveUpSec = 240;
    public const double MinConfidence = 0.45;
    private const int SampleEverySec = 5;

    private readonly List<Lane> _lanes = new();
    private int _lastSample = int.MinValue;
    private int _lastAttemptSampleCount = -1;
    private int? _lastHits;

    public bool Decided { get; private set; }
    public int Samples => _lanes.Count;

    /// <summary>One checked ranked queue role is authoritative; lane inference must not replace it.</summary>
    public void AcceptAssignedRole() => Decided = true;

    public void Reset()
    {
        _lanes.Clear();
        _lastSample = int.MinValue;
        _lastAttemptSampleCount = -1;
        _lastHits = null;
        Decided = false;
    }

    public void Observe(int clock, int? x, int? y, int? lastHits)
    {
        if (Decided || clock < FromSec || clock > GiveUpSec) return;
        if (lastHits != null) _lastHits = lastHits;
        if (x is not int px || y is not int py) return;
        if (_lastSample != int.MinValue && clock - _lastSample < SampleEverySec) return;
        _lastSample = clock;
        _lanes.Add(MapLanes.Classify(px, py));
    }

    /// <summary>
    /// Попробовать принять решение. Возвращает догадку один раз за матч (с 2:30) или null, если ещё рано
    /// или данных не хватило (тогда после 4:00 детектор сдаётся и ничего не меняет).
    /// </summary>
    public RoleGuess? TryDecide(int clock, Hero hero, string? team, Func<IEnumerable<MatchRecord>> history, Position current)
    {
        if (Decided || clock < DecideAtSec) return null;
        if (_lastAttemptSampleCount == _lanes.Count && clock < GiveUpSec) return null;
        _lastAttemptSampleCount = _lanes.Count;
        var known = _lanes.Where(l => l != Lane.Unknown).ToList();
        if (team is not ("radiant" or "dire") || known.Count < 6)
        {
            if (clock >= GiveUpSec) Decided = true;
            return null;
        }
        var top = known.GroupBy(l => l).OrderByDescending(g => g.Count()).First();
        double share = (double)top.Count() / Math.Max(1, _lanes.Count);
        if (share < 0.55)
        {
            if (clock >= GiveUpSec) Decided = true;
            return null;
        }
        var lane = top.Key;
        string laneRu, laneEn;
        Position[] candidates;
        if (lane == Lane.Mid) { candidates = new[] { Position.Mid }; laneRu = "центр"; laneEn = "mid lane"; }
        else if (lane == MapLanes.SafeLane(team)) { candidates = new[] { Position.Carry, Position.HardSupport }; laneRu = "лёгкая линия"; laneEn = "safe lane"; }
        else { candidates = new[] { Position.Offlane, Position.SoftSupport }; laneRu = "сложная линия"; laneEn = "off lane"; }

        if (candidates.Length == 1)
        {
            var midGuess = new RoleGuess(Position.Mid, Math.Round(share, 2), lane, laneRu, laneEn);
            Decided = midGuess.Confidence >= MinConfidence || clock >= GiveUpSec;
            return midGuess;
        }

        var mine = history().Where(m => m.HeroId == hero.Id && m.Position != null).ToList();
        double Score(Position p, out string whyRu, out string whyEn)
        {
            double s = 0;
            var notesRu = new List<string>();
            var notesEn = new List<string>();
            int rank = IndexOf(hero.Positions, p);
            if (rank >= 0) s += Math.Max(0, 3 - rank);
            int played = mine.Count(m => m.Position == p);
            if (played > 0) { s += Math.Min(played, 5) * 0.8; notesRu.Add($"вы играли им так {played}×"); notesEn.Add($"you played it this way {played}×"); }
            bool core = p is Position.Carry or Position.Offlane;
            if (_lastHits is int lh)
            {
                if (core && lh >= 10) s += 2.5;
                else if (core && lh >= 6) s += 1;
                else if (!core && lh <= 3) s += 2;
                else if (!core && lh <= 5) s += 0.8;
            }
            if (p == current) s += 1;
            whyRu = string.Join(", ", notesRu);
            whyEn = string.Join(", ", notesEn);
            return s;
        }
        var scored = candidates.Select(p => (P: p, S: Score(p, out var ru, out var en), Ru: ru, En: en))
            .OrderByDescending(x => x.S).ToList();
        var best = scored[0];
        double margin = best.S - scored[1].S;
        double confidence = Math.Round(Math.Clamp(0.5 + margin / 6, 0.3, 0.95) * share, 2);
        string lhRu = _lastHits is int l ? $", добиваний {l}" : "";
        string lhEn = _lastHits is int l2 ? $", {l2} last hits" : "";
        Decided = confidence >= MinConfidence || clock >= GiveUpSec;
        return new RoleGuess(best.P, confidence, lane,
            laneRu + lhRu + (best.Ru.Length > 0 ? ", " + best.Ru : ""),
            laneEn + lhEn + (best.En.Length > 0 ? ", " + best.En : ""));
    }

    private static int IndexOf(IReadOnlyList<Position> list, Position p)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == p) return i;
        return -1;
    }
}

/// <summary>
/// Проверка роли по фарму на 10:00: в поиске можно получить поз. 5, а играть кором (Radiance и 35+ добиваний к 10 минуте),
/// и наоборот. Пороги те же, что у разбора после игры (<see cref="MatchStory.PlaysCore"/>), чтобы подсказки в матче
/// и итог говорили об одной и той же роли. Срабатывает один раз за матч; ручной выбор роли не трогает.
/// </summary>
public sealed class FarmRoleCheck
{
    public const int AtSec = 600;
    public const int CoreLastHits = 35;
    public const int SupportLastHits = 15;
    private const int LaneFromSec = 420;
    private readonly List<Lane> _lanes = new();
    private int _lastSample = int.MinValue;

    public bool Done { get; private set; }

    public void Reset() { _lanes.Clear(); _lastSample = int.MinValue; Done = false; }

    /// <summary>Где герой стоит с 7:00 до 10:00 — по этому выбирается позиция внутри кора или саппорта.</summary>
    public void Observe(int clock, int? x, int? y)
    {
        if (Done || clock < LaneFromSec || clock > AtSec || x is not int px || y is not int py) return;
        if (_lastSample != int.MinValue && clock - _lastSample < 5) return;
        _lastSample = clock;
        _lanes.Add(MapLanes.Classify(px, py));
    }

    /// <summary>Новая позиция, если фарм к 10:00 явно не совпадает с текущей ролью; иначе null. Решение принимается один раз.</summary>
    public RoleGuess? TryDecide(int clock, int? lastHits, Position current, string? team, Hero? hero)
    {
        if (Done || clock < AtSec || lastHits is not int lh) return null;
        Done = true;
        if (clock > AtSec + 120) return null;   // ROSHIQ подключился поздно: к 20-й минуте у саппорта бывает и 35 добиваний
        bool core = current is Position.Carry or Position.Mid or Position.Offlane;
        var lane = MainLane();
        if (!core && lh >= CoreLastHits)
        {
            var p = lane == Lane.Mid ? Position.Mid
                : team is "radiant" or "dire" && lane == MapLanes.SafeLane(team) ? Position.Carry
                : team is "radiant" or "dire" && lane == MapLanes.OffLane(team) ? Position.Offlane
                : FirstOf(hero, Position.Carry, Position.Mid, Position.Offlane) ?? Position.Carry;
            return new RoleGuess(p, 0.8, lane, $"{lh} добиваний к 10:00 — это фарм кора", $"{lh} last hits by 10:00 is core farm");
        }
        if (core && lh < SupportLastHits)
        {
            var p = team is "radiant" or "dire" && lane == MapLanes.SafeLane(team) ? Position.HardSupport
                : team is "radiant" or "dire" && lane == MapLanes.OffLane(team) ? Position.SoftSupport
                : FirstOf(hero, Position.SoftSupport, Position.HardSupport) ?? Position.SoftSupport;
            return new RoleGuess(p, 0.8, lane, $"всего {lh} добиваний к 10:00 — это игра саппорта", $"only {lh} last hits by 10:00 is support play");
        }
        return null;
    }

    private static Position? FirstOf(Hero? hero, params Position[] set)
    {
        if (hero == null) return null;
        foreach (var p in hero.Positions) if (set.Contains(p)) return p;
        return null;
    }

    private Lane MainLane()
    {
        var known = _lanes.Where(l => l != Lane.Unknown).ToList();
        if (known.Count < 6) return Lane.Unknown;
        var top = known.GroupBy(l => l).OrderByDescending(g => g.Count()).First();
        return top.Count() * 2 >= known.Count ? top.Key : Lane.Unknown;
    }
}

/// <summary>
/// Следит, запущена ли Dota 2: решение принимается после нескольких одинаковых проверок подряд, чтобы не мигать.
/// Первое устоявшееся состояние (игра уже была открыта при запуске ROSHIQ) событием не считается.
/// </summary>
public sealed class GameLaunchWatcher
{
    private readonly int _onAfter;
    private readonly int _offAfter;
    private int _streak;
    private bool? _lastSeen;
    private bool _stable;

    public GameLaunchWatcher(int onAfter = 3, int offAfter = 5) { _onAfter = onAfter; _offAfter = offAfter; }

    public bool Running { get; private set; }

    /// <summary>Возвращает true/false при смене состояния (игра запустилась/закрылась), иначе null.</summary>
    public bool? Observe(bool found)
    {
        if (_lastSeen == found) _streak++;
        else { _streak = 1; _lastSeen = found; }
        if (_streak < (found ? _onAfter : _offAfter)) return null;
        if (!_stable) { _stable = true; Running = found; return null; }
        if (found == Running) return null;
        Running = found;
        return found;
    }
}
