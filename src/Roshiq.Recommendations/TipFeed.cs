namespace Roshiq.Recommendations;

/// <summary>Что стало с подсказкой матча: показана, сохранена для разбора, помогла (условие ушло) или пропущена.</summary>
public enum TipFeedStatus { Shown, Saved, Helped, Missed }

public sealed record TipFeedEntry(int Clock, string Kind, string Text, TipFeedStatus Status, string? AlertKey);

/// <summary>
/// Лента подсказок текущего матча для экрана «Обзор»: время, текст и исход. Живёт только в памяти и только один матч.
/// Исход «помогло» / «пропущено» приходит позже показа — из счётчиков <see cref="MatchAlerts"/>.
/// </summary>
public sealed class TipFeed
{
    public const int Max = 40;
    private readonly List<TipFeedEntry> _items = new();

    /// <summary>Растёт при каждом изменении — интерфейс перерисовывает список только когда он изменился.</summary>
    public int Revision { get; private set; }
    public IReadOnlyList<TipFeedEntry> Items => _items;
    public int ShownCount => _items.Count(e => e.Status != TipFeedStatus.Saved);
    public int SavedCount => _items.Count(e => e.Status == TipFeedStatus.Saved);

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Revision++;
    }

    public void Add(int clock, string kind, string text, TipFeedStatus status, string? alertKey = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _items.Add(new TipFeedEntry(Math.Max(0, clock), kind, text.Trim(), status, alertKey));
        if (_items.Count > Max) _items.RemoveAt(0);
        Revision++;
    }

    /// <summary>
    /// Сверить с накопленными счётчиками матча: на каждое новое «выполнено» / «проигнорировано» самая ранняя ещё не оценённая
    /// показанная подсказка этого вида получает исход.
    /// </summary>
    public void Sync(IReadOnlyDictionary<string, int> followed, IReadOnlyDictionary<string, int> ignored)
    {
        Apply(followed, TipFeedStatus.Helped);
        Apply(ignored, TipFeedStatus.Missed);
    }

    private void Apply(IReadOnlyDictionary<string, int> counts, TipFeedStatus status)
    {
        foreach (var (key, count) in counts)
        {
            int have = _items.Count(e => e.AlertKey == key && e.Status == status);
            while (have < count)
            {
                int i = _items.FindIndex(e => e.AlertKey == key && e.Status == TipFeedStatus.Shown);
                if (i < 0) break;
                _items[i] = _items[i] with { Status = status };
                have++;
                Revision++;
            }
        }
    }

    /// <summary>Последние <paramref name="count"/> записей, от ранних к поздним.</summary>
    public IReadOnlyList<TipFeedEntry> Recent(int count) => _items.Skip(Math.Max(0, _items.Count - count)).ToList();
}
