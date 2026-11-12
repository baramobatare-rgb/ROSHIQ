using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Ближайшее событие матча: когда наступит (игровые часы) и через сколько секунд.</summary>
public sealed record UpcomingEvent(TimerRule Rule, int AtSec, int InSec);

/// <summary>
/// Расписание периодических событий по игровым часам (clock_time GSI). Используются только
/// общеизвестные правила игры из timers.txt — никакой информации о противнике.
/// </summary>
public static class GameTimers
{
    /// <summary>Следующее появление строго после clock (или первое, если оно ещё не наступило).</summary>
    public static int? NextOccurrence(TimerRule rule, int clock)
    {
        int at;
        if (clock < rule.FirstSec) at = rule.FirstSec;
        else if (rule.IntervalSec <= 0) return null;
        else
        {
            long n = ((long)clock - rule.FirstSec) / rule.IntervalSec + 1;
            long next = rule.FirstSec + n * rule.IntervalSec;
            if (next > int.MaxValue) return null;
            at = (int)next;
        }
        if (rule.LastSec is int last && at > last) return null;
        return at;
    }

    public static IReadOnlyList<UpcomingEvent> Upcoming(IEnumerable<TimerRule> rules, int clock, Func<string, bool> enabled, int max = 4)
    {
        var list = new List<UpcomingEvent>();
        foreach (var rule in rules)
        {
            if (!enabled(rule.Key)) continue;
            if (NextOccurrence(rule, clock) is int at) list.Add(new UpcomingEvent(rule, at, at - clock));
        }
        return list.OrderBy(e => e.InSec).ThenBy(e => e.Rule.Key, StringComparer.Ordinal).Take(max).ToList();
    }

    /// <summary>Формат «1:05» / «-0:30».</summary>
    public static string Clock(int seconds)
    {
        var abs = Math.Abs((long)seconds);
        return $"{(seconds < 0 ? "-" : "")}{abs / 60}:{abs % 60:00}";
    }
}

/// <summary>
/// Однократные предупреждения «скоро событие»: каждое появление объявляется не больше одного раза,
/// когда до него осталось не больше LeadSec. Перемотка часов назад (новый матч) сбрасывает состояние.
/// </summary>
public sealed class TimerAnnouncer
{
    private readonly HashSet<(string Key, int At)> _announced = new();
    private int? _lastClock;

    public void Reset()
    {
        _announced.Clear();
        _lastClock = null;
    }

    /// <summary>События, о которых нужно предупредить прямо сейчас.</summary>
    public IReadOnlyList<UpcomingEvent> Due(IEnumerable<TimerRule> rules, int clock, Func<string, bool> enabled)
    {
        if (_lastClock is int prev && clock + 30 < prev) _announced.Clear();
        // пропуск вперёд (переподключение) — не объявлять то, что уже прошло
        _lastClock = clock;
        var due = new List<UpcomingEvent>();
        foreach (var e in GameTimers.Upcoming(rules, clock, enabled, int.MaxValue))
        {
            if (e.InSec <= 0 || e.InSec > e.Rule.LeadSec) continue;
            if (_announced.Add((e.Rule.Key, e.AtSec))) due.Add(e);
        }
        if (_announced.Count > 200) _announced.RemoveWhere(x => x.At < clock - 60);
        return due;
    }
}

public enum RoshanPhase { None, Dead, MaybeAlive }

/// <summary>Состояние после отмеченного убийства Рошана (по игровым часам).</summary>
public sealed record RoshanState(RoshanPhase Phase, int KilledAt, int AegisLeftSec, int RespawnMinAt, int RespawnMaxAt);

/// <summary>
/// Таймер Рошана по ручной отметке игрока (горячая клавиша или кнопка), как секундомер.
/// Возрождение — от 8 до 11 минут после убийства, Аегис действует 5 минут.
/// </summary>
public sealed class RoshanTracker
{
    public const int AegisSec = 300;
    public const int RespawnMinSec = 480;
    public const int RespawnMaxSec = 660;

    private readonly HashSet<string> _announced = new();

    public int? KilledAt { get; private set; }

    public void MarkKilled(int clock)
    {
        KilledAt = clock;
        _announced.Clear();
    }

    public void Clear()
    {
        KilledAt = null;
        _announced.Clear();
    }

    public RoshanState State(int clock)
    {
        if (KilledAt is not int k) return new RoshanState(RoshanPhase.None, 0, 0, 0, 0);
        int since = clock - k;
        if (since < -30 || since > RespawnMaxSec + 120)
        {
            // часы ушли назад (новый матч) или прошло слишком много — отметка больше не актуальна
            Clear();
            return new RoshanState(RoshanPhase.None, 0, 0, 0, 0);
        }
        int aegis = Math.Max(0, AegisSec - Math.Max(0, since));
        var phase = since < RespawnMinSec ? RoshanPhase.Dead : RoshanPhase.MaybeAlive;
        return new RoshanState(phase, k, aegis, k + RespawnMinSec, k + RespawnMaxSec);
    }

    /// <summary>Ключи предупреждений, которые нужно показать сейчас: aegis_30, respawn_min, respawn_max.</summary>
    public IReadOnlyList<string> Due(int clock)
    {
        var st = State(clock);
        var list = new List<string>();
        if (st.Phase == RoshanPhase.None) return list;
        int since = clock - st.KilledAt;
        void Check(string key, bool cond) { if (cond && _announced.Add(key)) list.Add(key); }
        Check("rosh_aegis_soon", since >= AegisSec - 30 && since < AegisSec);
        Check("rosh_respawn_min", since >= RespawnMinSec && since < RespawnMinSec + 30);
        Check("rosh_respawn_max", since >= RespawnMaxSec && since < RespawnMaxSec + 30);
        return list;
    }
}

/// <summary>
/// 0.21.5: таймер глифа врагов по ручной отметке (как Рошан): перезарядка 5 минут.
/// Глиф обновляется раньше, если команда теряет первую вышку T1/T2 или первый барак ближнего боя — об этом честно говорим в подсказке.
/// </summary>
public sealed class GlyphTracker
{
    public const int CooldownSec = 300;
    private bool _announced;

    public int? UsedAt { get; private set; }

    public void MarkUsed(int clock) { UsedAt = clock; _announced = false; }

    public void Clear() { UsedAt = null; _announced = false; }

    /// <summary>Сколько секунд до готовности глифа; null — отметки нет (или она устарела).</summary>
    public int? ReadyIn(int clock)
    {
        if (UsedAt is not int u) return null;
        int left = u + CooldownSec - clock;
        if (clock < u - 30 || left < -60) { Clear(); return null; }   // новый матч или давно готов
        return Math.Max(0, left);
    }

    /// <summary>true один раз — в момент, когда глиф врагов снова готов.</summary>
    public bool DueReady(int clock)
    {
        if (ReadyIn(clock) is not int left || left > 0 || _announced) return false;
        _announced = true;
        return true;
    }
}
