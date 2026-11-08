using Roshiq.Core.Models;
using Roshiq.Core.Settings;

namespace Roshiq.Recommendations;

/// <summary>Что известно о своём герое из GSI на текущий момент.</summary>
public sealed record InventoryState(
    int Clock,
    bool Alive,
    int? Gold,
    int? GoldUnreliable,
    IReadOnlyDictionary<string, string> Slots,
    Position Position,
    int? BuybackCost = null,
    int? BuybackCooldown = null,
    int? SavingFor = null);   // цена следующего предмета сборки: если золота меньше — игрок копит, «много золота» не подсказываем

/// <summary>
/// Ситуативные подсказки по собственному инвентарю и золоту: нет телепорта, предметы лежат в тайнике,
/// много золота при себе. Ничего не покупает и не нажимает — только напоминает.
/// Подсказка срабатывает, если условие держится не меньше SustainSec, и повторяется не чаще CooldownSec.
/// </summary>
public sealed class MatchAlerts
{
    public const string NoTeleport = "alert_no_tp";
    public const string WardsInStash = "alert_wards_stash";
    public const string ItemsInStash = "alert_stash";
    public const string UnspentGold = "alert_gold";
    public const string NoBuyback = "alert_buyback";

    /// <summary>Все ключи подсказок (для настроек и статистики).</summary>
    public static readonly string[] AllKeys = { NoTeleport, WardsInStash, ItemsInStash, UnspentGold, NoBuyback };
    /// <summary>Подсказка считается проигнорированной, если условие держится столько секунд после показа.</summary>
    public const int IgnoreAfterSec = 60;

    private static readonly Dictionary<string, (int SustainSec, int CooldownSec)> Timing = new()
    {
        [NoTeleport] = (20, 150),
        [WardsInStash] = (15, 120),
        [ItemsInStash] = (40, 180),
        [UnspentGold] = (30, 240),
        [NoBuyback] = (15, 300),
    };

    private readonly Dictionary<string, int> _since = new();
    private readonly Dictionary<string, int> _lastShown = new();
    private int? _lastClock;
    private readonly HashSet<(string Key, int Shown)> _ignoreChecked = new();
    private readonly Dictionary<string, int> _prevShown = new();

    /// <summary>
    /// Подсказка, которую вернул <see cref="Tick"/>, так и не попала на экран (очередь, оверлей скрыт, стала неактуальна).
    /// Откатываем отметку показа — иначе через минуту её бы посчитали проигнорированной, и адаптация показывала бы её реже.
    /// </summary>
    public void Unseen(string key)
    {
        // оверлей скрыт или уведомления выключены: перерыв оставляем (иначе предлагали бы каждую секунду),
        // но в статистику «показано/проигнорировано» эта попытка не идёт
        if (!_lastShown.TryGetValue(key, out var shown)) return;
        _ignoreChecked.Add((key, shown));
        if (Counts.TryGetValue(key, out var c)) { if (c <= 1) Counts.Remove(key); else Counts[key] = c - 1; }
    }

    public void Retract(string key)
    {
        if (!_lastShown.TryGetValue(key, out var shown)) return;
        _ignoreChecked.Add((key, shown));
        if (_prevShown.TryGetValue(key, out var before)) { _lastShown[key] = before; _prevShown.Remove(key); }
        else _lastShown.Remove(key);
        if (Counts.TryGetValue(key, out var c)) { if (c <= 1) Counts.Remove(key); else Counts[key] = c - 1; }
    }

    /// <summary>Множители интервала повтора: часто игнорируемые подсказки показываются реже.</summary>
    public Dictionary<string, double> CooldownMultipliers { get; } = new();
    /// <summary>Сколько раз за матч подсказку проигнорировали.</summary>
    public Dictionary<string, int> Ignored { get; } = new();
    /// <summary>Подсказка показана, и в течение минуты условие ушло (купил телепорт, забрал из тайника…).</summary>
    public Dictionary<string, int> Followed { get; } = new();

    /// <summary>Сколько раз за матч сработала каждая подсказка (для разбора после игры).</summary>
    public Dictionary<string, int> Counts { get; } = new();

    public void Reset()
    {
        _since.Clear();
        _lastShown.Clear();
        _lastClock = null;
        Counts.Clear();
        Ignored.Clear();
        Followed.Clear();
        _prevShown.Clear();
        _ignoreChecked.Clear();
    }

    /// <summary>
    /// Множители по последним 10 матчам: если подсказку показывали хотя бы 4 раза и в 60% случаев условие
    /// не исправляли минуту спустя — интервал ×3, при 40% — ×2.
    /// </summary>
    public static Dictionary<string, double> AdaptiveMultipliers(IEnumerable<MatchRecord> history)
    {
        var recent = history.Where(m => m.FromGsi).OrderByDescending(m => m.StartedUtc).Take(10).ToList();
        var result = new Dictionary<string, double>();
        foreach (var key in AllKeys)
        {
            int fired = recent.Sum(m => m.Alerts.TryGetValue(key, out var c) ? c : 0);
            int ignored = recent.Sum(m => m.AlertsIgnored.TryGetValue(key, out var c) ? c : 0);
            if (fired < 4) continue;
            double ratio = (double)ignored / fired;
            if (ratio >= 0.6) result[key] = 3.0;
            else if (ratio >= 0.4) result[key] = 2.0;
        }
        return result;
    }

    /// <summary>Условия, которые выполняются прямо сейчас (без учёта задержек).</summary>
    public static IReadOnlyList<string> Conditions(InventoryState s, AlertSettings settings, int teleportCost = 100)
    {
        var list = new List<string>();
        if (!settings.Enabled || !s.Alive || s.Clock < 0) return list;
        bool Has(string slotPrefix, Func<string, bool> pred) =>
            s.Slots.Any(kv => kv.Key.StartsWith(slotPrefix, StringComparison.Ordinal) && kv.Value != "empty" && pred(kv.Value));

        if (settings.NoTeleport && s.Clock >= 120 && s.Slots.Count > 0 && (s.Gold ?? 0) >= teleportCost
            && !Has("teleport", v => v == "item_tpscroll") && !Has("slot", v => v is "item_tpscroll" or "item_travel_boots" or "item_travel_boots_2"))
            list.Add(NoTeleport);

        if (settings.Stash)
        {
            bool wards = Has("stash", v => v is "item_ward_observer" or "item_ward_sentry" or "item_ward_dispenser");
            if (wards && s.Position is Position.SoftSupport or Position.HardSupport) list.Add(WardsInStash);
            else if (Has("stash", _ => true)) list.Add(ItemsInStash);
        }

        int atRisk = s.GoldUnreliable ?? s.Gold ?? 0;
        bool saving = s.SavingFor is int target && target > 0 && (s.Gold ?? 0) < target;
        // запас на выкуп: только если цена точно известна и выкуп готов (в позднюю игру). Тогда «много золота» —
        // это и золото под риском, и остаток сверх выкупа не меньше порога. Неизвестный выкуп — как раньше.
        bool reserveKnown = s.Clock >= settings.BuybackFromMinute * 60 && s.BuybackCost is int reserve && reserve > 0
                            && s.BuybackCooldown == 0 && s.Gold is not null;
        int spare = reserveKnown ? s.Gold!.Value - s.BuybackCost!.Value : atRisk;
        if (settings.UnspentGold && atRisk >= settings.UnspentGoldThreshold && spare >= settings.UnspentGoldThreshold && !saving)
            list.Add(UnspentGold);

        if (settings.Buyback && s.Clock >= settings.BuybackFromMinute * 60 && s.BuybackCost is int bb && bb > 0
            && s.BuybackCooldown == 0 && s.Gold is int gold && gold < bb)
            list.Add(NoBuyback);
        return list;
    }

    /// <summary>Вызывать раз в секунду. Возвращает подсказку, которую пора показать (не больше одной).</summary>
    public string? Tick(InventoryState s, AlertSettings settings, int teleportCost = 100)
    {
        if (_lastClock is int prev && s.Clock + 30 < prev) Reset();
        _lastClock = s.Clock;

        var active = Conditions(s, settings, teleportCost);
        foreach (var key in _since.Keys.ToList())
            if (!active.Contains(key)) _since.Remove(key);

        // смерть снимает подсказки — это не «выполнил» и не «проигнорировал»: закрываем показы без оценки
        if (!s.Alive)
            foreach (var (key, shown) in _lastShown)
                if (s.Clock - shown < IgnoreAfterSec) _ignoreChecked.Add((key, shown));

        // показали, и в течение минуты условие ушло, пока герой жив, — подсказку выполнили
        foreach (var (key, shown) in _lastShown)
            if (s.Alive && !active.Contains(key) && s.Clock - shown < IgnoreAfterSec && _ignoreChecked.Add((key, shown)))
                Followed[key] = Followed.TryGetValue(key, out var f) ? f + 1 : 1;

        // показали, а через минуту условие всё ещё держится — подсказку проигнорировали
        foreach (var key in active)
            if (_lastShown.TryGetValue(key, out var shown) && s.Clock - shown >= IgnoreAfterSec && _since.TryGetValue(key, out var since)
                && since <= shown && _ignoreChecked.Add((key, shown)))
                Ignored[key] = Ignored.TryGetValue(key, out var n) ? n + 1 : 1;

        string? result = null;
        foreach (var key in active)
        {
            if (!_since.ContainsKey(key)) _since[key] = s.Clock;
            var (sustain, baseCooldown) = Timing[key];
            int cooldown = settings.Adaptive && CooldownMultipliers.TryGetValue(key, out var mult) ? (int)(baseCooldown * mult) : baseCooldown;
            if (s.Clock - _since[key] < sustain) continue;
            if (_lastShown.TryGetValue(key, out var last) && s.Clock - last < cooldown) continue;
            if (result != null) continue;
            result = key;
            if (_lastShown.TryGetValue(key, out var before)) _prevShown[key] = before; else _prevShown.Remove(key);
            _lastShown[key] = s.Clock;
            Counts[key] = Counts.TryGetValue(key, out var c) ? c + 1 : 1;
        }
        return result;
    }
}
