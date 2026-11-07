namespace Roshiq.Core.Text;

/// <summary>
/// Нечёткий поиск для Ctrl+K: регистр не важен, ё = е, начало слова важнее середины, допускаются аббревиатуры
/// («bkb», «аб») и набор не в той раскладке («gelf» → «пуда», «gel;» → «пудж»).
/// </summary>
public static class QuickSearch
{
    private const string En = "`qwertyuiop[]asdfghjkl;'zxcvbnm,.";
    private const string Ru = "ёйцукенгшщзхъфывапролджэячсмитьбю";

    public static string Normalize(string s) =>
        new string(s.Trim().ToLowerInvariant().Replace('ё', 'е').Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());

    /// <summary>Строка, набранная не в той раскладке (английская ↔ русская), или null, если менять нечего.</summary>
    public static string? SwapLayout(string s)
    {
        var lower = s.ToLowerInvariant();
        bool changed = false;
        var chars = lower.Select(c =>
        {
            int i = En.IndexOf(c);
            if (i >= 0) { changed = true; return Ru[i]; }
            i = Ru.IndexOf(c);
            if (i >= 0) { changed = true; return En[i]; }
            return c;
        }).ToArray();
        return changed ? new string(chars) : null;
    }

    /// <summary>Оценка совпадения запроса с текстом (0 — нет совпадения, больше — лучше).</summary>
    public static int Score(string query, string text)
    {
        var q = Normalize(query);
        var t = Normalize(text);
        if (q.Length == 0 || t.Length == 0) return 0;
        if (t == q) return 1000;
        if (t.StartsWith(q, StringComparison.Ordinal)) return 800 - Math.Min(200, t.Length - q.Length);
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q, StringComparison.Ordinal))) return 600 - Math.Min(100, t.Length - q.Length);
        // аббревиатура по первым буквам слов: «bkb» → Black King Bar
        if (q.Length >= 2 && words.Length >= q.Length)
        {
            var initials = new string(words.Select(w => w[0]).ToArray());
            if (initials.StartsWith(q, StringComparison.Ordinal)) return 550;
        }
        int idx = t.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0) return 400 - Math.Min(150, idx * 5);
        // буквы по порядку с пропусками («blnk» → blink)
        if (q.Length >= 3 && Subsequence(q, t, out int gaps)) return Math.Max(1, 200 - gaps * 10);
        return 0;
    }

    /// <summary>Лучшая оценка по названию и синонимам, с учётом неправильной раскладки (чуть ниже).</summary>
    public static int Best(string query, IEnumerable<string> texts)
    {
        var list = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        int best = list.Count == 0 ? 0 : list.Max(t => Score(query, t));
        if (SwapLayout(query) is string swapped)
        {
            int alt = list.Count == 0 ? 0 : list.Max(t => Score(swapped, t));
            if (alt > 0) best = Math.Max(best, alt - 50);
        }
        return best;
    }

    private static bool Subsequence(string q, string t, out int gaps)
    {
        gaps = 0;
        int j = 0, last = -1;
        foreach (char c in q)
        {
            if (c == ' ') continue;
            while (j < t.Length && t[j] != c) j++;
            if (j >= t.Length) return false;
            if (last >= 0 && j - last > 1) gaps++;
            last = j++;
        }
        return true;
    }
}
