namespace Roshiq.Vision;

/// <summary>Confirm a pick only after two consecutive matches. Never promote uncertain candidates.</summary>
public sealed class DraftPickTracker
{
    private sealed class SlotState
    {
        public string? Candidate;
        public string? Confirmed;
        public int Streak;
        public int Misses;
    }
    private readonly Dictionary<(Side, int), SlotState> _slots = new();
    public void Reset() => _slots.Clear();

    public void Apply(DraftRecognitionResult result)
    {
        var seen = new HashSet<(Side, int)>();
        foreach (var slot in result.Slots)
        {
            var key = (slot.Region.Side, slot.Region.Index);
            if (!seen.Add(key)) continue;
            if (!_slots.TryGetValue(key, out var state)) _slots[key] = state = new();
            if (slot.Match.Status == SlotStatus.Recognized && slot.Match.HeroKey is { } hero)
            {
                state.Misses = 0;
                state.Streak = state.Candidate == hero ? state.Streak + 1 : 1;
                state.Candidate = hero;
                if (state.Streak >= 2) state.Confirmed = hero;
            }
            else
            {
                state.Streak = 0; state.Candidate = null;
                state.Misses++;
                // A confirmed pick may disappear briefly under a tooltip or animation.
                if (state.Misses >= 3) state.Confirmed = null;
            }
        }
        foreach (var key in _slots.Keys.Where(k => !seen.Contains(k)).ToList()) _slots.Remove(key);
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
