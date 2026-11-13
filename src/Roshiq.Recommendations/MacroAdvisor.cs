using Roshiq.Core.Models;

namespace Roshiq.Recommendations;

/// <summary>Короткий совет «что делать сейчас» (0.23.1).</summary>
public sealed record MacroTip(string Key, string Ru, string En)
{
    public string Text(Language lang) => lang == Language.English ? En : Ru;
}

/// <summary>
/// 0.23.1: подсказки по макроигре из того, что Dota честно отдаёт через GSI: счёт убийств сторон, время, день и ночь.
/// Не видим врагов на карте и их вышки — поэтому советуем по факту исхода драки, а не «где стоит враг».
/// </summary>
public sealed class MacroAdvisor
{
    /// <summary>Окно, в котором смотрим исход драки.</summary>
    public const int FightWindowSec = 25;
    /// <summary>Сколько убийств в окне делает «выигранную драку».</summary>
    public const int FightKills = 3;
    public const int CooldownSec = 75;

    private readonly Queue<(int Clock, int Ours, int Theirs)> _scores = new();
    private int _lastTipClock = -100_000;
    private int _lastNightTip = -100_000;
    private bool? _wasDay;

    public void Reset()
    {
        _scores.Clear();
        _lastTipClock = -100_000;
        _lastNightTip = -100_000;
        _wasDay = null;
    }

    /// <param name="ours">Убийства вашей стороны (radiant_score или dire_score).</param>
    public MacroTip? Observe(int clock, int? ours, int? theirs, bool? daytime, bool heroAlive)
    {
        MacroTip? tip = null;
        if (ours is int o && theirs is int t)
        {
            // новый матч или перемотка: счёт или время ушли назад
            if (_scores.Count > 0 && (clock < _scores.Last().Clock || o < _scores.Last().Ours || t < _scores.Last().Theirs)) Reset();
            _scores.Enqueue((clock, o, t));
            while (_scores.Count > 0 && _scores.Peek().Clock < clock - FightWindowSec) _scores.Dequeue();
            var first = _scores.Peek();
            int gained = o - first.Ours, lost = t - first.Theirs;
            if (clock - _lastTipClock >= CooldownSec)
            {
                if (gained >= FightKills && lost <= 1)
                    tip = clock >= 15 * 60
                        ? new MacroTip("won_fight", $"Выиграли драку ({gained} на {lost}) — давите вышку, а если враги долго мертвы, идите на Рошана.",
                            $"Fight won ({gained} for {lost}) — push a tower, and if they're dead for long, go for Roshan.")
                        : new MacroTip("won_fight", $"Выиграли драку ({gained} на {lost}) — давите ближайшую вышку, пока враги мертвы.",
                            $"Fight won ({gained} for {lost}) — push the nearest tower while they're dead.");
                else if (lost >= FightKills && gained <= 1)
                    tip = new MacroTip("lost_fight", heroAlive
                            ? $"Проиграли драку ({gained} на {lost}) — отходите под свои вышки и не ходите в одиночку, пока союзники не возродятся."
                            : $"Проиграли драку ({gained} на {lost}) — после возрождения не ходите в одиночку, ждите союзников.",
                        heroAlive
                            ? $"Fight lost ({gained} for {lost}) — fall back to your towers and don't go alone until allies respawn."
                            : $"Fight lost ({gained} for {lost}) — after respawning, don't go alone, wait for your allies.");
                if (tip != null) { _lastTipClock = clock; _scores.Clear(); _scores.Enqueue((clock, o, t)); }
            }
        }

        // ночь: напоминаем не чаще раза в 10 минут, с 5:00
        if (tip == null && daytime is bool day)
        {
            if (_wasDay == true && !day && clock >= 5 * 60 && clock - _lastNightTip >= 600 && clock - _lastTipClock >= CooldownSec)
            {
                tip = new MacroTip("night", "Наступила ночь: обзор у всех меньше — удобно ганкать, а в чужом лесу будьте осторожнее.",
                    "Night has fallen: everyone sees less — good for ganks, but be careful in the enemy jungle.");
                _lastNightTip = clock;
                _lastTipClock = clock;
            }
            _wasDay = day;
        }
        return tip;
    }
}
