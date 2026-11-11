namespace Roshiq.Vision;

/// <summary>Confirm a pick only after two consecutive matches. Never promote uncertain candidates.</summary>
public sealed class DraftPickTracker
{
    private sealed class SlotState
    {
        public string? Candidate;
        public string? Confirmed;
        public int Streak;
        /// <summary>Сколько кадров подряд слот уверенно пуст (а не просто неразборчив).</summary>
        public int Empties;
    }
    private readonly Dictionary<(Side, int), SlotState> _slots = new();
    public void Reset() => _slots.Clear();

    public void Apply(DraftRecognitionResult result)
    {
        // драфт скрыт (экран загрузки, показ команд) — кадр ничего не говорит о пиках
        if (!result.DraftUiVisible) return;
        var seen = new HashSet<(Side, int)>();
        foreach (var slot in result.Slots)
        {
            var key = (slot.Region.Side, slot.Region.Index);
            if (!seen.Add(key)) continue;
            if (!_slots.TryGetValue(key, out var state)) _slots[key] = state = new();
            if (slot.Match.Status == SlotStatus.Recognized && slot.Match.HeroKey is { } hero)
            {
                state.Empties = 0;
                state.Streak = state.Candidate == hero ? state.Streak + 1 : 1;
                state.Candidate = hero;
                if (state.Streak >= 2) state.Confirmed = hero;
            }
            else
            {
                state.Streak = 0; state.Candidate = null;
                // Пик в Dota не исчезает. Неразборчивый портрет (подсказка, анимация, экран стратегии в конце драфта —
                // 11.11.2026 так «пропали» 4 союзника) пик не снимает; снимает только уверенно пустой слот (перевыбор).
                state.Empties = slot.Match.Status == SlotStatus.Empty ? state.Empties + 1 : 0;
                if (state.Empties >= 3) state.Confirmed = null;
            }
        }
    }
    /// <summary>
    /// Союзники с экрана плюс свой герой из GSI: игра сама сообщает, кого ты взял, даже если портрет закрыт значком
    /// ранга (11.11.2026 Ogre Magi с «8» на портрете выпал из союзников). Героя, который виден у врагов, не добавляем.
    /// </summary>
    public static IReadOnlyList<string> WithOwnHero(IReadOnlyList<string> allies, IReadOnlyList<string> enemies, string? ownHero)
    {
        if (string.IsNullOrEmpty(ownHero) || allies.Count >= 5
            || allies.Contains(ownHero, StringComparer.OrdinalIgnoreCase)
            || enemies.Contains(ownHero, StringComparer.OrdinalIgnoreCase)) return allies;
        return allies.Append(ownHero).ToList();
    }

    public IReadOnlyList<string> Heroes(Side side)
    {
        // Duplicate portraits across slots/sides are not a trustworthy pick.
        var duplicates = _slots.Values.Where(s => s.Confirmed != null).GroupBy(s => s.Confirmed)
            .Where(g => g.Count() > 1).Select(g => g.Key!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _slots.Where(p => p.Key.Item1 == side && p.Value.Confirmed != null && !duplicates.Contains(p.Value.Confirmed))
            .OrderBy(p => p.Key.Item2).Select(p => p.Value.Confirmed!).ToList();
    }
}
