namespace Roshiq.Recommendations;

/// <summary>Срочность совета: срочные идут первыми и с минимальной паузой, фоновые (служебные) — после матча.</summary>
public enum TipPriority { Background = 0, Normal = 1, Urgent = 2 }

/// <summary>Что стало с советом: показан, не показан (оверлей отказал), истёк срок, стал неактуален, заменён более свежим.</summary>
public enum TipOutcome { Shown, Dropped, Expired, Invalidated, Merged, Deferred }

/// <summary>Один совет для всплывающего уведомления.</summary>
public sealed class Tip
{
    /// <summary>Вид совета для статистики («alert_no_tp», «pace_lh», «spike», «service»…).</summary>
    public required string Kind { get; init; }
    /// <summary>Ключ объединения: новый совет с тем же ключом заменяет ожидающий. По умолчанию — вид.</summary>
    public string? Key { get; init; }
    public required string Header { get; init; }
    public required string Text { get; init; }
    public TipPriority Priority { get; set; } = TipPriority.Normal;
    public bool Warning { get; init; }
    /// <summary>Только во время матча с живыми данными GSI (не на паузе). Служебные сообщения — false.</summary>
    public bool InGameOnly { get; init; }
    /// <summary>Показывать, даже когда герой мёртв (например, про выкуп).</summary>
    public bool AllowWhenDead { get; init; }
    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Проверка актуальности перед показом: false — совет снимается без показа.</summary>
    public Func<bool>? StillValid { get; init; }
    /// <summary>Произвольные данные источника (например, ключ подсказки инвентаря).</summary>
    public object? Tag { get; init; }
    public DateTime CreatedUtc { get; internal set; }

    public string EffectiveKey => Key ?? Kind;
}

/// <summary>Обстановка в момент выбора совета.</summary>
public readonly record struct TipContext(bool Blocked, bool InGame, bool Paused, bool Stale, bool Dead);

/// <summary>
/// Единый диспетчер советов. Все источники кладут совет в очередь; диспетчер раз в секунду выбирает один —
/// по срочности, потом по возрасту — с паузами между показами, объединяет дубли и снимает просроченные и неактуальные.
/// Ничего не теряется молча: у каждого совета есть исход (<see cref="Outcome"/>), по нему видно, что реально увидел игрок.
/// </summary>
public sealed class TipDispatcher
{
    /// <summary>Минимальная пауза после любого показа (для срочных).</summary>
    public TimeSpan UrgentGap { get; init; } = TimeSpan.FromSeconds(4);
    /// <summary>Обычный совет — не чаще, чем раз в столько после предыдущего показа.</summary>
    public TimeSpan NormalGap { get; init; } = TimeSpan.FromSeconds(12);
    /// <summary>Фоновый (служебный) — ещё реже и не во время матча.</summary>
    public TimeSpan BackgroundGap { get; init; } = TimeSpan.FromSeconds(20);

    private readonly List<Tip> _queue = new();
    private DateTime _lastShownUtc = DateTime.MinValue;

    public event Action<Tip, TipOutcome>? Outcome;

    public int Pending => _queue.Count;
    public IReadOnlyList<Tip> Queue => _queue;

    /// <summary>Положить совет в очередь. Ожидающий с тем же ключом заменяется (срочность берётся наибольшая).</summary>
    public void Enqueue(Tip tip, DateTime nowUtc)
    {
        tip.CreatedUtc = nowUtc;
        var same = _queue.FirstOrDefault(t => t.EffectiveKey == tip.EffectiveKey);
        if (same != null)
        {
            _queue.Remove(same);
            if (same.Priority > tip.Priority) tip.Priority = same.Priority;
            Outcome?.Invoke(same, TipOutcome.Merged);
        }
        _queue.Add(tip);
    }

    /// <summary>
    /// Совет, который пора показать, или null. Перед выбором снимает истёкшие и неактуальные.
    /// После показа вызывающий сообщает результат через <see cref="Report"/>.
    /// </summary>
    public Tip? Next(TipContext ctx, DateTime nowUtc)
    {
        foreach (var t in _queue.ToList())
        {
            TipOutcome? gone = nowUtc - t.CreatedUtc > t.Ttl ? TipOutcome.Expired
                : t.StillValid != null && !SafeValid(t) ? TipOutcome.Invalidated : null;
            if (gone == null) continue;
            _queue.Remove(t);
            Outcome?.Invoke(t, gone.Value);
        }
        if (ctx.Blocked || _queue.Count == 0) return null;

        var since = nowUtc - _lastShownUtc;
        var best = _queue
            .Where(t => !t.InGameOnly || (ctx.InGame && !ctx.Paused && !ctx.Stale))
            .Where(t => !ctx.Dead || t.AllowWhenDead || !t.InGameOnly)
            .Where(t => t.Priority != TipPriority.Background || !ctx.InGame)       // служебное — после матча
            .OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedUtc)
            .FirstOrDefault();
        if (best == null) return null;
        var gap = best.Priority switch
        {
            TipPriority.Urgent => UrgentGap,
            TipPriority.Normal => NormalGap,
            _ => BackgroundGap
        };
        if (since < gap) return null;
        _queue.Remove(best);
        return best;
    }

    /// <summary>Итог показа: true — совет на экране, false — оверлей не показал (выключен, скрыт).</summary>
    public void Report(Tip tip, bool shown, DateTime nowUtc)
    {
        if (shown) _lastShownUtc = nowUtc;
        Outcome?.Invoke(tip, shown ? TipOutcome.Shown : TipOutcome.Dropped);
    }

    /// <summary>Сбросить очередь (новый матч): оставшиеся считаются истёкшими.</summary>
    public void Clear(Func<Tip, bool>? which = null)
    {
        foreach (var t in _queue.Where(t => which?.Invoke(t) ?? true).ToList())
        {
            _queue.Remove(t);
            Outcome?.Invoke(t, TipOutcome.Expired);
        }
    }

    private static bool SafeValid(Tip t)
    {
        try { return t.StillValid!(); }
        catch { return false; }
    }
}
