using System.Windows.Media;
using Roshiq.App.Views;
using Roshiq.Core.Localization;
using Roshiq.Core.Text;

namespace Roshiq.App.Services;

/// <summary>Один результат поиска Ctrl+K.</summary>
public sealed record SearchEntry(string Title, string Subtitle, string KindKey, IReadOnlyList<string> Terms, Action Open,
    Func<ImageSource?>? Icon = null, int Boost = 0);

/// <summary>
/// Что ищет Ctrl+K: разделы, настройки (по ключевым словам на двух языках), герои, предметы и таймеры.
/// Индекс собирается при каждом открытии — он маленький и всегда на текущем языке.
/// </summary>
public static class SearchIndex
{
    public static IReadOnlyList<SearchEntry> Build(AppHost host)
    {
        var L = Loc.Instance;
        bool ru = L.Language == Roshiq.Core.Models.Language.Russian;
        var list = new List<SearchEntry>();

        // --- разделы
        foreach (var key in new[] { "dashboard", "draft", "tierlist", "build", "assistant", "history", "settings" })
            list.Add(new(L.Get("sec_" + key), L.Get("sec_" + key + "_sub"), "srch_kind_section",
                new[] { L.Get("sec_" + key), key }, () => MainWindow.Navigate(key), Boost: 40));

        // --- настройки и инструменты: (ключ названия, синонимы, куда)
        void Setting(string titleKey, string where, Action open, params string[] terms) =>
            list.Add(new(L.Get(titleKey), where, "srch_kind_setting", terms.Prepend(L.Get(titleKey)).ToList(), open, Boost: 30));
        string inGeneral = L.Get("srch_in_settings"), inOverlay = L.Get("srch_in_overlay"), inData = L.Get("srch_in_data"),
            inMatch = L.Get("srch_in_match"), inRem = L.Get("srch_in_reminders");
        Setting("set_diagnostics", L.Get("srch_tool"), DiagnosticsWindow.Open, "диагностика", "не работает", "проблема", "ошибка", "diagnostics", "broken", "gsi", "порт");
        Setting("set_run_wizard", L.Get("srch_tool"), () => new FirstRunWindow { Owner = System.Windows.Application.Current.MainWindow }.ShowDialog(), "мастер", "настроить", "wizard", "setup");
        Setting("preset_title", inGeneral, () => MainWindow.Navigate("settings/general"), "режим", "минимум", "подсказки", "preset", "minimal", "hints");
        Setting("snd_title", inGeneral, () => MainWindow.Navigate("settings/general"), "звук", "громкость", "sound", "volume", "beep");
        Setting("srch_gsi", inGeneral, () => MainWindow.Navigate("settings/general"), "gsi", "game state integration", "связь с игрой", "порт", "port", "cfg");
        Setting("srch_steam", inGeneral, () => MainWindow.Navigate("settings/general"), "steam", "стим", "аккаунт", "профиль", "account");
        Setting("srch_language", inGeneral, () => MainWindow.Navigate("settings/general"), "язык", "language", "english", "русский", "тема", "theme", "цвет");
        Setting("srch_autostart", inGeneral, () => MainWindow.Navigate("settings/general"), "автозапуск", "windows", "трей", "tray", "autostart");
        Setting("srch_automation", inGeneral, () => MainWindow.Navigate("settings/general"), "автоматика", "роль", "импорт", "разбор", "automation", "итог матча", "post match");
        Setting("ov_hotkeys", inOverlay, () => MainWindow.Navigate("settings/overlay"), "горячие клавиши", "клавиша", "f8", "hotkey", "shortcut", "keys");
        Setting("ov_edit_start", inOverlay, () => host.Overlay.SetEditMode(true), "расставить", "переместить", "положение", "двигать", "move", "position", "drag", "layout");
        Setting("srch_overlay", inOverlay, () => MainWindow.Navigate("settings/overlay"), "оверлей", "прозрачность", "масштаб", "профиль", "стрим", "overlay", "opacity", "scale", "stream");
        Setting("fresh_title", inData, () => MainWindow.Navigate("settings/data"), "данные", "патч", "обновление", "версия", "data", "patch", "update", "opendota");
        Setting("srch_alerts", inMatch, () => MainWindow.Navigate("assistant/match"), "тайник", "телепорт", "тп", "выкуп", "золото", "stash", "teleport", "tp", "buyback", "gold");
        Setting("srch_reminders", inRem, () => MainWindow.Navigate("assistant/reminders"), "варды", "смок", "напоминания", "ward", "smoke", "reminder", "обсервер", "сентри");
        Setting("srch_roshan", inMatch, () => MainWindow.Navigate("assistant/match"), "рошан", "аегис", "roshan", "aegis", "таймер");
        Setting("srch_pool", L.Get("sec_draft"), () => MainWindow.Navigate("draft"), "пул", "избранные", "pool", "favorites", "контрпик", "counter");
        Setting("srch_goals", L.Get("sec_history"), () => MainWindow.Navigate("history"), "цель", "цели", "goal", "тренер", "coach", "закономерности", "patterns");

        // --- таймеры
        foreach (var t in host.Database.Timers)
            list.Add(new(t.Name(L.Language), L.Format("srch_timer_detail", Clock(t.FirstSec), Clock(t.IntervalSec)), "srch_kind_timer",
                new[] { t.Ru, t.En, t.Key }, () => MainWindow.Navigate("assistant/match")));

        // --- герои
        foreach (var h in host.Database.Heroes)
        {
            var hero = h;
            list.Add(new(ru ? h.NameRu : h.Name, ru ? h.Name : h.NameRu, "srch_kind_hero", new[] { h.Name, h.NameRu, h.Key.Replace("npc_dota_hero_", "") },
                () => { HeroesView.PendingHeroKey = hero.Key; MainWindow.Navigate("build"); }, () => HeroArt.Portrait(hero.Key)));
        }

        // --- предметы
        foreach (var i in host.Database.Items)
        {
            var item = i;
            list.Add(new(i.Name, i.Purpose(L.Language), "srch_kind_item", new[] { i.Name, i.NameRu, i.Key.Replace("item_", "").Replace('_', ' ') },
                () => MainWindow.Navigate("build"), () => HeroArt.ItemIcon(item.Key)));
        }
        return list;
    }

    /// <summary>Лучшие результаты для запроса (до max). Пустой запрос — разделы и инструменты.</summary>
    public static IReadOnlyList<SearchEntry> Find(IReadOnlyList<SearchEntry> index, string query, int max = 12)
    {
        if (string.IsNullOrWhiteSpace(query))
            return index.Where(e => e.KindKey is "srch_kind_section" or "srch_kind_setting").Take(max).ToList();
        return index.Select(e => (e, score: QuickSearch.Best(query, e.Terms.Append(e.Title))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score + x.e.Boost)
            .ThenBy(x => x.e.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(max).Select(x => x.e).ToList();
    }

    private static string Clock(int s) => s <= 0 ? "0:00" : $"{s / 60}:{s % 60:00}";
}
