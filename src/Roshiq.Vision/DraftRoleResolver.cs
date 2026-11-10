using Roshiq.Core.Models;

namespace Roshiq.Vision;

public sealed record DraftOcrWord(string Text, int X1, int Y1, int X2, int Y2);

/// <summary>Maps OCR words from the ranked draft header to the local player's assigned role.</summary>
public static class DraftRoleResolver
{
    public static Position? Resolve(IReadOnlyList<DraftOcrWord> words, string playerName,
        IReadOnlyList<SlotRegion> slots, int frameHeight)
    {
        var wanted = Normalize(playerName);
        if (wanted.Length < 4) return null;
        var allies = slots.Where(s => s.Side == Side.Radiant).OrderBy(s => s.Index).ToList();
        if (allies.Count != 5) return null;
        var matches = new List<Position>();
        foreach (var slot in allies)
        {
            // Dota always displays the local team in the left five columns.
            int left = slot.X - 32, right = slot.X + slot.Width + 32;
            var name = string.Concat(words.Where(w => w.Y1 >= frameHeight * 0.55 && w.Y1 < frameHeight * 0.75 &&
                (w.X1 + w.X2) / 2 >= left && (w.X1 + w.X2) / 2 <= right)
                .OrderBy(w => w.X1).Select(w => Normalize(w.Text)));
            if (name.Length < 4 ||
                (!name.Contains(wanted, StringComparison.Ordinal) && !wanted.Contains(name, StringComparison.Ordinal))) continue;
            var role = string.Concat(words.Where(w => w.Y1 >= frameHeight * 0.74 && w.Y1 < frameHeight * 0.95 &&
                (w.X1 + w.X2) / 2 >= left && (w.X1 + w.X2) / 2 <= right)
                .OrderBy(w => w.X1).Select(w => Normalize(w.Text)));
            var position = ParseRole(role);
            if (position != null) matches.Add(position.Value);
        }
        return matches.Count == 1 ? matches[0] : null;
    }

    private static Position? ParseRole(string text)
    {
        if (text.Contains("полнаяподдержка", StringComparison.Ordinal)) return Position.HardSupport;
        if (text.Contains("поддержка", StringComparison.Ordinal)) return Position.SoftSupport;
        if (text.Contains("лёгкая", StringComparison.Ordinal) || text.Contains("легкая", StringComparison.Ordinal)) return Position.Carry;
        if (text.Contains("сложная", StringComparison.Ordinal)) return Position.Offlane;
        if (text.Contains("центр", StringComparison.Ordinal)) return Position.Mid;
        return null;
    }

    private static string Normalize(string? value) => new((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
