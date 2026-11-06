using Roshiq.Core.Models;
using Roshiq.Core.Settings;

namespace Roshiq.Recommendations;

public enum ReminderKind { ObserverWard, SentryWard, Smoke, Consumables }

public sealed record Reminder(ReminderKind Kind, TimeSpan ShownAt, TimeSpan HideAt)
{
    public string TextKey => Kind switch
    {
        ReminderKind.ObserverWard => "rem_observer",
        ReminderKind.SentryWard => "rem_sentry",
        ReminderKind.Smoke => "rem_smoke",
        _ => "rem_consumables"
    };
}

/// <summary>
/// Ненавязчивые напоминания для 4/5 позиции. Работают строго по расписанию (игровое время из GSI
/// или локальный таймер) и не используют никакой информации о противнике.
/// Гарантии: одновременно активно не более одного напоминания; каждое скрывается через DisplaySec;
/// между напоминаниями выдерживается MinGapSec.
/// </summary>
public sealed class SupportReminderScheduler
{
    private readonly Dictionary<ReminderKind, TimeSpan> _nextDue = new();
    private NotificationSettings _settings;
    private TimeSpan _lastShownEnd = TimeSpan.MinValue;
    private TimeSpan? _lastTime;

    public SupportReminderScheduler(NotificationSettings settings)
    {
        _settings = settings;
        Reset();
    }

    public Reminder? Active { get; private set; }

    public event EventHandler<Reminder>? Shown;
    public event EventHandler? Hidden;

    public void ApplySettings(NotificationSettings settings)
    {
        _settings = settings;
        if (!settings.Enabled) Dismiss();
    }

    /// <summary>Сброс расписания (новая игра).</summary>
    public void Reset()
    {
        var first = TimeSpan.FromSeconds(_settings.FirstReminderAtSec);
        _nextDue[ReminderKind.ObserverWard] = first;
        _nextDue[ReminderKind.SentryWard] = first + TimeSpan.FromSeconds(Math.Min(_settings.SentryIntervalSec, 180));
        _nextDue[ReminderKind.Smoke] = TimeSpan.FromSeconds(Math.Max(_settings.FirstReminderAtSec, 240));
        _nextDue[ReminderKind.Consumables] = first + TimeSpan.FromSeconds(Math.Min(_settings.ConsumablesIntervalSec, 240));
        _lastShownEnd = TimeSpan.MinValue;
        _lastTime = null;
        Dismiss();
    }

    public void Dismiss()
    {
        if (Active == null) return;
        Active = null;
        Hidden?.Invoke(this, EventArgs.Empty);
    }

    public static bool AppliesTo(Position position) => position is Position.SoftSupport or Position.HardSupport;

    /// <summary>
    /// Вызывать периодически (раз в секунду). gameTime — игровые часы (может быть отрицательным в фазе подготовки).
    /// Возвращает напоминание, которое нужно показать прямо сейчас, иначе null.
    /// </summary>
    public Reminder? Tick(TimeSpan gameTime, Position position, bool gameInProgress)
    {
        // время пошло назад (новый матч / перезапуск) — начинаем заново
        if (_lastTime != null && gameTime + TimeSpan.FromSeconds(30) < _lastTime.Value) Reset();
        _lastTime = gameTime;

        if (Active != null && gameTime >= Active.HideAt)
        {
            _lastShownEnd = Active.HideAt;
            Dismiss();
        }

        if (!_settings.Enabled || !AppliesTo(position) || !gameInProgress) return null;
        if (Active != null) return null; // не больше одного напоминания одновременно
        if (_lastShownEnd != TimeSpan.MinValue && gameTime < _lastShownEnd + TimeSpan.FromSeconds(_settings.MinGapSec)) return null;

        var due = _nextDue
            .Where(kv => IsKindEnabled(kv.Key) && gameTime >= kv.Value)
            .OrderBy(kv => kv.Value)
            .Select(kv => (ReminderKind?)kv.Key)
            .FirstOrDefault();
        if (due == null) return null;

        var kind = due.Value;
        _nextDue[kind] = gameTime + TimeSpan.FromSeconds(IntervalOf(kind));
        Active = new Reminder(kind, gameTime, gameTime + TimeSpan.FromSeconds(_settings.DisplaySec));
        Shown?.Invoke(this, Active);
        return Active;
    }

    public TimeSpan? NextDue(ReminderKind kind) => _nextDue.TryGetValue(kind, out var t) ? t : null;

    private bool IsKindEnabled(ReminderKind kind) => kind switch
    {
        ReminderKind.ObserverWard => _settings.ObserverEnabled,
        ReminderKind.SentryWard => _settings.SentryEnabled,
        ReminderKind.Smoke => _settings.SmokeEnabled,
        _ => _settings.ConsumablesEnabled
    };

    private int IntervalOf(ReminderKind kind) => kind switch
    {
        ReminderKind.ObserverWard => _settings.ObserverIntervalSec,
        ReminderKind.SentryWard => _settings.SentryIntervalSec,
        ReminderKind.Smoke => _settings.SmokeIntervalSec,
        _ => _settings.ConsumablesIntervalSec
    };
}
