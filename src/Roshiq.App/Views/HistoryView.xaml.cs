using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

/// <summary>05 / История: записанные матчи, разбор матча (график, тайминги предметов), статистика и импорт OpenDota.</summary>
public partial class HistoryView : UserControl, IRefreshable
{
    private bool _ready;
    private MatchRecord? _selected;

    /// <summary>Матч, который выбрать при следующем обновлении (переход из итога матча).</summary>
    public static string? PendingSelectId { get; set; }

    public HistoryView()
    {
        InitializeComponent();
        Ui.HeroTemplate(MatchList);
        App.Host.History.Changed += (_, _) => Dispatcher.InvokeAsync(() => { if (IsLoaded) Refresh(); });
        App.Host.Reviews.Saved += (_, id) => Dispatcher.InvokeAsync(() => { if (IsLoaded && _selected?.MatchId == id && !_reviewBusy) ShowMatch(_selected); });
        App.Host.SteamChanged += (_, _) => Dispatcher.InvokeAsync(() => { if (IsLoaded) ShowPatterns(App.Host.History.All()); });
        App.Host.Goals.Changed += (_, _) => Dispatcher.InvokeAsync(() => { if (IsLoaded) ShowGoals(); });
        Loc.Instance.PropertyChanged += (_, _) => { if (IsLoaded) Refresh(); };
    }

    private static string HeroName(MatchRecord m) =>
        App.Host.Database.FindHero(m.HeroId)?.Name ?? App.Host.Database.FindHero(m.HeroKey)?.Name ?? "?";

    private static string ResultText(MatchResult r) => Loc.T(r switch
    {
        MatchResult.Win => "hist_win",
        MatchResult.Loss => "hist_loss",
        _ => "hist_unknown"
    });

    public void Refresh()
    {
        _ready = false;
        var L = Loc.Instance;
        var all = App.Host.History.All();
        var keep = PendingSelectId ?? _selected?.Id;
        PendingSelectId = null;
        MatchList.Items.Clear();
        foreach (var m in all)
        {
            var date = m.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
            var hero = App.Host.Database.FindHero(m.HeroId) ?? App.Host.Database.FindHero(m.HeroKey);
            MatchList.Items.Add(new Choice<MatchRecord>(m,
                $"{date} · {HeroName(m)} · {ResultText(m.Result)} · {GameTimers.Clock(m.DurationSec)}{(m.FromGsi ? "" : " · OD")}")
            {
                Icon = Roshiq.App.Services.HeroArt.Portrait(hero?.Key),
                Group = null
            });
        }
        CountText.Text = all.Count == 0 ? L.Get("hist_empty_short") : L.Format("hist_count", all.Count);
        SteamIdBox.Text = App.Host.Settings.Current.Data.SteamAccountId ?? "";
        _ready = true;

        var toSelect = MatchList.Items.OfType<Choice<MatchRecord>>().FirstOrDefault(c => c.Value.Id == keep)
                       ?? MatchList.Items.OfType<Choice<MatchRecord>>().FirstOrDefault();
        MatchList.SelectedItem = toSelect;
        if (toSelect == null) ShowMatch(null);
        ShowStats(all);
        ShowGoals();
        ShowTrends(all);
        ShowPatterns(all);
    }

    /// <summary>Переключение вкладок раздела с мягким появлением содержимого.</summary>
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (MatchesSection == null || ProgressSection == null || ImportSection == null) return; // вызов из InitializeComponent
        MatchesSection.Visibility = TabMatches.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ProgressSection.Visibility = TabProgress.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ImportSection.Visibility = TabImport.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var shown = TabMatches.IsChecked == true ? MatchesSection : TabProgress.IsChecked == true ? ProgressSection : ImportSection;
        Motion.Enter(shown, 0, 12);
    }

    private void ShowGoals()
    {
        var L = Loc.Instance;
        var goals = App.Host.Goals;
        var active = goals.Active;
        GoalText.Text = active == null ? L.Get("goal_none") : App.Host.GoalText(active);
        GoalProgress.Text = active == null ? L.Get("goal_none_hint")
            : L.Format("goal_progress", active.Achieved, GoalEngine.AchieveToClose, active.Results.Count, GoalEngine.MaxAttempts);
        GoalSkip.Visibility = active == null ? Visibility.Collapsed : Visibility.Visible;
        GoalHistory.Children.Clear();
        foreach (var (goal, result) in goals.RecentResults(10).Reverse())
        {
            var chip = Ui.Chip(result.Achieved ? "✓" : "✗", result.Achieved ? "AccentSoft" : "Panel3", result.Achieved ? "Accent" : "Red");
            chip.ToolTip = App.Host.GoalText(goal) + " · " + result.Utc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
            GoalHistory.Children.Add(chip);
        }
    }

    private void GoalSkip_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Goals.Skip();
        ShowGoals();
    }

    /// <summary>Четыре мини-графика: GPM, смерти, добивания к 10:00 и доля побед.</summary>
    private void ShowTrends(IReadOnlyList<MatchRecord> all)
    {
        var L = Loc.Instance;
        TrendGrid.Children.Clear();
        var series = new[]
        {
            (Trends.Build("gpm", all, m => m.Gpm > 0 ? m.Gpm : null), L.Get("trend_gpm"), true),
            (Trends.Build("deaths", all, m => m.DurationSec > 0 ? m.Deaths : null), L.Get("trend_deaths"), false),
            (Trends.Build("lh10", all, m => m.LastHitsAt(600)), L.Get("trend_lh10"), true),
        };
        foreach (var (s, title, higherIsBetter) in series) TrendGrid.Children.Add(TrendCard(s, title, higherIsBetter));

        var (recent, previous) = Trends.WinRate(all);
        var wr = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        wr.Children.Add(Ui.Text(Ui.Up(L.Get("trend_wr")), "Eyebrow"));
        var wrValue = Ui.Text("—", "BigValue");
        if (recent is double r) Motion.CountUp(wrValue, r * 100, "0", "%");
        wr.Children.Add(wrValue);
        wr.Children.Add(Ui.Text(previous is double p && recent is double r2
            ? L.Format("trend_wr_change", p.ToString("P0", CultureInfo.InvariantCulture), Signed((r2 - p) * 100, "0") + " п.п.")
            : L.Get("trend_wr_few"), "MutedText"));
        TrendGrid.Children.Add(wr);
    }

    private static string Signed(double v, string format) => (v > 0 ? "+" : "") + v.ToString(format, CultureInfo.InvariantCulture);

    private FrameworkElement TrendCard(TrendSeries s, string title, bool higherIsBetter)
    {
        var L = Loc.Instance;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        panel.Children.Add(Ui.Text(Ui.Up(title), "Eyebrow"));
        var canvas = new Canvas { Height = 56, ClipToBounds = true };
        panel.Children.Add(new Border
        {
            Height = 58, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = Ui.Res("Line"), Background = Ui.Res("Panel2"), Child = canvas
        });
        canvas.SizeChanged += (_, _) => DrawSpark(canvas, s);
        string text;
        if (s.Points.Count < 6) text = L.Format("trend_few", s.Points.Count);
        else
        {
            bool better = s.After > s.Before == higherIsBetter;
            var change = s.Change is double c ? Signed(c * 100, "0") + "%" : "";
            text = L.Format(better ? "trend_better" : "trend_worse", s.Before!.Value.ToString("0.#", CultureInfo.InvariantCulture),
                s.After!.Value.ToString("0.#", CultureInfo.InvariantCulture), change);
        }
        panel.Children.Add(Ui.Text(text, "MutedText", 4));
        return panel;
    }

    private static void DrawSpark(Canvas canvas, TrendSeries s)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (s.Points.Count < 2 || w < 20 || h < 20) return;
        double min = s.Points.Min(p => p.Value), max = s.Points.Max(p => p.Value);
        if (max - min < 1e-6) { max += 1; min -= 1; }
        var line = new Polyline { Stroke = Ui.Res("Accent"), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round };
        for (int i = 0; i < s.Points.Count; i++)
        {
            double x = 6 + (w - 12) * i / (s.Points.Count - 1);
            double y = 6 + (h - 12) * (1 - (s.Points[i].Value - min) / (max - min));
            line.Points.Add(new Point(x, y));
        }
        canvas.Children.Add(line);
    }

    private void OpenDota_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.MatchId is string id) Roshiq.App.Services.AppUpdater.OpenPage("https://www.opendota.com/matches/" + Uri.EscapeDataString(id));
    }

    private void Stratz_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.MatchId is string id) Roshiq.App.Services.AppUpdater.OpenPage("https://stratz.com/matches/" + Uri.EscapeDataString(id));
    }

    private void MatchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ShowMatch((MatchList.SelectedItem as Choice<MatchRecord>)?.Value);
    }

    private void ShowMatch(MatchRecord? m)
    {
        var L = Loc.Instance;
        _selected = m;
        DeleteButton.IsEnabled = m != null;
        if (m == null)
        {
            ReviewPanel.Visibility = Visibility.Collapsed;
            EmptyState.Content = Ui.State(UiState.Empty, L.Get("hist_empty_title"), L.Get("hist_empty_text"));
            return;
        }
        EmptyState.Content = null;
        ReviewPanel.Visibility = Visibility.Visible;
        LinksPanel.Visibility = m.MatchId != null ? Visibility.Visible : Visibility.Collapsed;

        MatchTitle.Text = HeroName(m);
        var matchHero = App.Host.Database.FindHero(m.HeroId) ?? App.Host.Database.FindHero(m.HeroKey);
        MatchPortrait.Content = matchHero != null ? Ui.Portrait(matchHero, 96) : null;
        Motion.Enter(ReviewPanel, 0, 8);
        var role = m.Position is Position p ? L.PositionName(p) : L.Get("hist_role_unknown");
        MatchMeta.Text = $"{m.StartedUtc.ToLocalTime():g} · {role} · {(m.Team ?? "?").ToUpperInvariant()} · {L.Get(m.FromGsi ? "hist_source_gsi" : "hist_source_od")}" +
                         (m.MatchId != null ? $" · ID {m.MatchId}" : "");

        TakeawaysPanel.Children.Clear();
        if (m.DurationSec >= PostMatch.MinDurationSec)
        {
            var summary = PostMatch.Build(m, App.Host.History.All(), k => App.Host.Database.FindItem(k)?.Name);
            TakeawaysPanel.Children.Add(new MatchTakeaways(summary));
        }
        SummaryChips.Children.Clear();
        SummaryChips.Children.Add(Ui.Chip(Ui.Up(ResultText(m.Result)), m.Result == MatchResult.Win ? "AccentSoft" : "Panel3",
            m.Result == MatchResult.Win ? "Accent" : m.Result == MatchResult.Loss ? "Red" : "Muted"));
        SummaryChips.Children.Add(Ui.Chip(L.Format("hist_duration", GameTimers.Clock(m.DurationSec))));
        SummaryChips.Children.Add(Ui.Chip($"K/D/A {m.Kills}/{m.Deaths}/{m.Assists}"));
        SummaryChips.Children.Add(Ui.Chip($"GPM {m.Gpm} · XPM {m.Xpm}"));
        SummaryChips.Children.Add(Ui.Chip($"LH {m.LastHits}" + (m.Denies > 0 ? $" / DN {m.Denies}" : "")));
        if (m.Level > 0) SummaryChips.Children.Add(Ui.Chip(L.Format("hist_level", m.Level)));
        ShowOdReview(m);

        DrawChart();

        ItemsPanel.Children.Clear();
        var history = App.Host.History.All();
        int i = 1;
        foreach (var (key, at) in m.ItemTimings.OrderBy(x => x.Value))
        {
            var item = App.Host.Database.FindItem(key);
            string name = item?.Name ?? Pretty(key);
            var bench = MatchStats.Benchmark(history, m, key);
            string detail = bench == null
                ? L.Get("hist_bench_none")
                : L.Format(bench.SameHero ? "hist_bench_hero" : "hist_bench_role", GameTimers.Clock(bench.AvgSec), bench.Matches)
                  + " · " + Delta(at - bench.AvgSec);
            ItemsPanel.Children.Add(Ui.Numbered(i++, name, detail, GameTimers.Clock(at), Ui.ItemIcon(key)));
        }
        if (i == 1) ItemsPanel.Children.Add(Ui.State(UiState.Empty, L.Get(m.FromGsi ? "hist_items_none" : "hist_items_od"), ""));

        ShowDeaths(m);

        AlertsPanel.Children.Clear();
        if (!m.FromGsi) AlertsPanel.Children.Add(Ui.Text(L.Get("hist_alerts_od"), "MutedText"));
        else if (m.Alerts.Count == 0) AlertsPanel.Children.Add(Ui.Text(L.Get("hist_alerts_none"), "MutedText"));
        else
            foreach (var (key, count) in m.Alerts.OrderByDescending(x => x.Value))
                AlertsPanel.Children.Add(Ui.Row(AlertName(key), "", "×" + count, "Warning"));
    }

    private static string AlertName(string key) => Loc.T(key + "_name");

    /// <summary>Разбор смертей: когда и с каким золотом, был ли телепорт и байбэк.</summary>
    private void ShowDeaths(MatchRecord m)
    {
        var L = Loc.Instance;
        DeathsPanel.Children.Clear();
        var d = DeathAnalysis.Analyze(m);
        if (d == null) { DeathsPanel.Children.Add(Ui.Text(L.Get("deaths_od"), "MutedText")); return; }
        if (d.Total == 0)
        {
            DeathsPanel.Children.Add(Ui.Text(m.Deaths == 0 ? L.Get("deaths_none") : L.Get("deaths_not_recorded"), "MutedText"));
            return;
        }
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        chips.Children.Add(Ui.Chip(L.Format("deaths_phase", "0–10", d.Early)));
        chips.Children.Add(Ui.Chip(L.Format("deaths_phase", "10–25", d.Mid)));
        chips.Children.Add(Ui.Chip(L.Format("deaths_phase", "25+", d.Late)));
        DeathsPanel.Children.Add(chips);
        if (d.WithLotsOfGold > 0)
            DeathsPanel.Children.Add(Ui.Row(L.Format("deaths_gold", d.WithLotsOfGold, DeathAnalysis.LotsOfGold),
                L.Format("deaths_gold_detail", d.AvgGoldAtRisk, d.MaxGoldAtRisk), "×" + d.WithLotsOfGold, "Warning"));
        if (d.WithoutTeleport > 0)
            DeathsPanel.Children.Add(Ui.Row(L.Get("deaths_no_tp"), L.Get("deaths_no_tp_detail"), "×" + d.WithoutTeleport, "Warning"));
        if (d.LateWithoutBuyback > 0)
            DeathsPanel.Children.Add(Ui.Row(L.Get("deaths_no_bb"), L.Get("deaths_no_bb_detail"), "×" + d.LateWithoutBuyback, "Red"));
        if (d.WithLotsOfGold == 0 && d.WithoutTeleport == 0 && d.LateWithoutBuyback == 0)
            DeathsPanel.Children.Add(Ui.Text(L.Format("deaths_clean", d.AvgGoldAtRisk), "MutedText"));
        var times = string.Join(" · ", m.DeathEvents.Select(x => GameTimers.Clock(x.Clock)));
        DeathsPanel.Children.Add(Ui.Text(L.Format("deaths_times", times), "MutedText", 4));
    }

    private static string Pretty(string itemKey) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase((itemKey.StartsWith("item_") ? itemKey[5..] : itemKey).Replace('_', ' '));

    private static string Delta(int seconds)
    {
        var L = Loc.Instance;
        if (Math.Abs(seconds) < 30) return L.Get("hist_delta_same");
        return seconds < 0 ? L.Format("hist_delta_faster", GameTimers.Clock(-seconds)) : L.Format("hist_delta_slower", GameTimers.Clock(seconds));
    }

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    /// <summary>Линии GPM и XPM по точкам, записанным раз в 30 секунд.</summary>
    private void DrawChart()
    {
        Chart.Children.Clear();
        var m = _selected;
        double w = Chart.ActualWidth, h = Chart.ActualHeight;
        if (m == null || w < 40 || h < 40) return;
        var pts = m.Samples.Where(s => s.Clock >= 60 && (s.Gpm != null || s.Xpm != null)).OrderBy(s => s.Clock).ToList();
        if (pts.Count < 2)
        {
            ChartNote.Text = Loc.T(m.FromGsi ? "hist_chart_short" : "hist_chart_od");
            return;
        }
        ChartNote.Text = "";
        const double padL = 44, padR = 12, padT = 12, padB = 24;
        int minX = pts[0].Clock, maxX = pts[^1].Clock;
        double maxY = Math.Max(100, pts.Max(s => Math.Max(s.Gpm ?? 0, s.Xpm ?? 0)));
        maxY = Math.Ceiling(maxY / 100) * 100;
        double X(int clock) => padL + (w - padL - padR) * (clock - minX) / Math.Max(1, maxX - minX);
        double Y(double v) => padT + (h - padT - padB) * (1 - v / maxY);

        var grid = Ui.Res("Line");
        var faint = Ui.Res("Faint");
        for (int k = 0; k <= 4; k++)
        {
            double v = maxY * k / 4;
            Chart.Children.Add(new Line { X1 = padL, X2 = w - padR, Y1 = Y(v), Y2 = Y(v), Stroke = grid, StrokeThickness = 1 });
            var label = new TextBlock { Text = ((int)v).ToString(CultureInfo.InvariantCulture), Foreground = faint, FontSize = 10 };
            Canvas.SetLeft(label, 4);
            Canvas.SetTop(label, Y(v) - 7);
            Chart.Children.Add(label);
        }
        int step = (maxX - minX) > 2400 ? 600 : 300;
        for (int t = (minX / step + 1) * step; t < maxX; t += step)
        {
            var label = new TextBlock { Text = (t / 60).ToString(CultureInfo.InvariantCulture) + ":00", Foreground = faint, FontSize = 10 };
            Canvas.SetLeft(label, X(t) - 12);
            Canvas.SetTop(label, h - padB + 6);
            Chart.Children.Add(label);
        }

        void Series(Func<MatchSample, int?> value, string brush, double thickness)
        {
            var line = new Polyline { Stroke = Ui.Res(brush), StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round };
            foreach (var s in pts)
                if (value(s) is int v) line.Points.Add(new Point(X(s.Clock), Y(v)));
            if (line.Points.Count >= 2) Chart.Children.Add(line);
        }
        Series(s => s.Xpm, "Muted", 1.6);
        Series(s => s.Gpm, "Accent", 2.2);
    }

    private void ShowStats(IReadOnlyList<MatchRecord> all)
    {
        var L = Loc.Instance;
        string Wr(GroupStats g) => g.WinRate is double wr
            ? L.Format("hist_wr", wr.ToString("P0", CultureInfo.InvariantCulture), g.Wins, g.Losses)
            : L.Get("hist_wr_unknown");

        HeroStatsPanel.Children.Clear();
        int i = 1;
        foreach (var (heroId, g) in MatchStats.ByHero(all).Take(8))
        {
            var name = App.Host.Database.FindHero(heroId)?.Name ?? "#" + heroId;
            HeroStatsPanel.Children.Add(Ui.Numbered(i++, name,
                $"{Wr(g)} · GPM {g.AvgGpm:0} · KDA {g.AvgKills:0.#}/{g.AvgDeaths:0.#}/{g.AvgAssists:0.#}", L.Format("hist_games", g.Games)));
        }
        if (i == 1) HeroStatsPanel.Children.Add(Ui.State(UiState.Empty, L.Get("hist_empty_short"), ""));

        RoleStatsPanel.Children.Clear();
        i = 1;
        foreach (var (pos, g) in MatchStats.ByPosition(all))
            RoleStatsPanel.Children.Add(Ui.Numbered(i++, L.PositionName(pos), $"{Wr(g)} · GPM {g.AvgGpm:0} · XPM {g.AvgXpm:0}", L.Format("hist_games", g.Games)));
        if (i == 1) RoleStatsPanel.Children.Add(Ui.State(UiState.Empty, L.Get("hist_role_none"), ""));

        MistakesPanel.Children.Clear();
        i = 1;
        foreach (var (key, perMatch, total) in MatchStats.AlertAverages(all))
            MistakesPanel.Children.Add(Ui.Numbered(i++, AlertName(key), L.Format("hist_per_match", perMatch.ToString("0.#", CultureInfo.InvariantCulture)), "×" + total));
        if (i == 1) MistakesPanel.Children.Add(Ui.State(UiState.Empty, L.Get("hist_mistakes_none"), ""));
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        if (MessageBox.Show(Loc.T("hist_delete_confirm"), "ROSHIQ", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var id = _selected.Id;
        _selected = null;
        App.Host.History.Delete(id);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var L = Loc.Instance;
        var d = App.Host.Settings.Current.Data;
        var account = SteamIds.ToAccountId(SteamIdBox.Text);
        if (account == null) { ImportStatus.Text = L.Get("hist_steam_invalid"); return; }
        if (!d.AllowOnlineStats) { ImportStatus.Text = L.Get("hist_need_online"); return; }
        App.Host.Settings.Update(s => s.Data.SteamAccountId = SteamIdBox.Text.Trim());
        ImportButton.IsEnabled = false;
        ImportStatus.Text = L.Get("hist_importing");
        try
        {
            using var client = new OpenDotaClient(d.OpenDotaApiKey);
            var rows = await client.GetRecentMatchesAsync(account.Value, CancellationToken.None);
            int added = App.Host.History.Import(rows);
            ImportStatus.Text = rows.Count == 0 ? L.Get("hist_import_empty") : L.Format("hist_import_done", added, rows.Count);
        }
        catch (Exception ex)
        {
            ImportStatus.Text = L.Format("hist_import_error", ex.Message);
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }
}
