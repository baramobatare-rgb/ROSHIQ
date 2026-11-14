namespace Roshiq.Recommendations;

/// <summary>
/// 0.24: «тишина в драке». По здоровью своего героя из GSI понимает, что идёт драка: за последние
/// <see cref="WindowSec"/> секунд потеряно не меньше <see cref="DropShare"/> максимального здоровья.
/// Драка считается идущей ещё <see cref="HoldSec"/> секунд после последней такой потери — в это время
/// обычные советы ждут, срочные (таймеры) показываются как раньше. Смерть героя драку заканчивает.
/// </summary>
public sealed class FightDetector
{
    public const double WindowSec = 3;
    public const double DropShare = 0.12;
    public const double HoldSec = 4;

    private readonly Queue<(DateTime At, int Health)> _samples = new();
    private DateTime _fightUntil = DateTime.MinValue;

    public bool InFight(DateTime nowUtc) => nowUtc < _fightUntil;

    public void Observe(DateTime nowUtc, int? health, int? maxHealth, bool? alive)
    {
        if (alive == false || health is not int hp || maxHealth is not int max || max <= 0)
        {
            Reset();
            return;
        }
        while (_samples.Count > 0 && (nowUtc - _samples.Peek().At).TotalSeconds > WindowSec) _samples.Dequeue();
        _samples.Enqueue((nowUtc, hp));
        int peak = _samples.Max(s => s.Health);
        if (peak - hp >= DropShare * max) _fightUntil = nowUtc.AddSeconds(HoldSec);
    }

    public void Reset()
    {
        _samples.Clear();
        _fightUntil = DateTime.MinValue;
    }
}
