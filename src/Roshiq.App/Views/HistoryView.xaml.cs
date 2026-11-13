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
    // 0.17.7: один снимок истории на обновление — его используют список, разбор матча и все вкладки
    private IReadOnlyList<MatchRecord> _all = Array.Empty<MatchRecord>();
    private string? _listKey;          // версия истории | язык | день, по которым построен список
    private bool _refreshQueued;       // несколько фоновых уведомлений подряд склеиваются в одно обновление
    private bool? _narrow;
    /// <summary>Если область уже этой ширины, разбор не помещается справа от списка — список встаёт сверху.</summary>
    private const double NarrowWidth = 900;

    /// <summary>Матч, который выбрать при следующем обновлении (переход из итога матча).</summary>
    public static string? PendingSelectId { get; set; }
    /// <summary>Вкладка, которую открыть при следующем показе («progress», «import», «matches»).</summary>
    public static string? PendingTab { get; set; }
    public static HistoryView? Instance { get; private set; }

    public void SelectTab(string tab)
    {
        (tab switch { "progress" => TabProgress, "import" => TabImport, _ => TabMatches }).IsChecked = true;
    }

    public HistoryView()
    {
        InitializeComponent();
        Instance = this;
        Loaded += (_, _) => { if (PendingTab is { } tab) { PendingTab = null; SelectTab(tab); } };
        Ui.HeroTemplate(MatchList);
        Ui.HeroTemplate(CompareHero);
        App.Host.History.Changed += (_, _) => RequestRefresh();
        App.Host.Reviews.Saved += (_, id) => Dispatcher.InvokeAsync(() =>
        {
            if (!IsLoaded) return;
            FillCompareHeroes();
            if (_selected?.MatchId == id && !_reviewBusy) ShowMatch(_selected);
        });
        App.Host.SteamChanged += (_, _) => Dispatcher.InvokeAsync(() => { if (IsLoaded) ShowPatterns(_all); });
        App.Host.Goals.Changed += (_, _) => Dispatcher.InvokeAsync(() => { if (IsLoaded) ShowGoals(); });
        Loc.Instance.PropertyChanged += (_, _) => { _listKey = null; RequestRefresh(); };
    }

    /// <summary>Обновить при ближайшем простое; повторные запросы до этого момента склеиваются в один.</summary>
    private void RequestRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _refreshQueued = false;
            if (IsLoaded) Refresh();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void MatchesGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyMatchesLayout(e.NewSize.Width);

    /// <summary>Узкое окно (или крупный масштаб): список матчей над разбором, во всю ширину.</summary>
    private void ApplyMatchesLayout(double width)
    {
        bool narrow = width > 0 && width < NarrowWidth;
        if (_narrow == narrow) return;
        _narrow = narrow;
        ListColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(340);
        ReviewColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(ReviewCard, narrow ? 1 : 0);
        Grid.SetColumn(ReviewCard, narrow ? 0 : 1);
        ListCard.Margin = narrow ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 8, 16);
        ReviewCard.Margin = narrow ? new Thickness(0, 0, 0, 16) : new Thickness(8, 0, 0, 16);
        MatchList.Height = narrow ? 280 : 420;
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
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
        var L = Loc.Instance;
        var key = App.Host.History.Revision + "|" + L.Language + "|" + DateTime.Now.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (key == _listKey && MatchList.Items.Count > 0)
        {
            // история не менялась (переход на вкладку) — список и прокрутку не трогаем; только запрошенный матч
            if (PendingSelectId is { } wanted)
            {
                PendingSelectId = null;
                if (MatchList.Items.OfType<Choice<MatchRecord>>().FirstOrDefault(c => c.Value.Id == wanted) is { } item)
                {
                    MatchList.SelectedItem = item;
                    MatchList.ScrollIntoView(item);
                }
            }
            return;
        }
        _listKey = key;
        _ready = false;
        var all = _all = App.Host.History.All();
        var keep = PendingSelectId ?? _selected?.Id;
        bool jumpTo = PendingSelectId != null;
        PendingSelectId = null;
        var scroll = FindScroll(MatchList);
        double offset = scroll?.VerticalOffset ?? 0;
        MatchList.Items.Clear();
        foreach (var m in all)
        {
            var date = m.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
            var hero = App.Host.Database.FindHero(m.HeroId) ?? App.Host.Database.FindHero(m.HeroKey);
            // карточка в две строки: «герой · итог» и «дата · длительность»; неполнота — видимой пометкой
            MatchList.Items.Add(new Choice<MatchRecord>(m, $"{HeroName(m)} · {ResultText(m.Result)}")
            {
                Subtitle = $"{date} · {GameTimers.Clock(m.DurationSec)}{(m.FromGsi ? "" : " · OpenDota")}"
                           + (m.Score is int sc ? " · " + L.Format("score_short", sc) : ""),
                Note = m.Partial ? Loc.T("hist_partial") : null,
                Icon = Roshiq.App.Services.HeroArt.Portrait(hero?.Key),
                // итог читается с одного взгляда: полоска в цвете победы / поражения
                Stripe = m.Result switch { MatchResult.Win => Ui.Res("Accent"), MatchResult.Loss => Ui.Res("Red"), _ => null },
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
        // фоновое обновление не сбрасывает прокрутку списка; переход к конкретному матчу — показывает его
        if (jumpTo && toSelect != null) MatchList.ScrollIntoView(toSelect);
        else if (scroll != null && offset > 0)
            Dispatcher.InvokeAsync(() => scroll.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
        ShowStats(all);
        ShowRecurring(all);
        FillCompareHeroes();
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
            (Trends.Build("score", all, m => m.Score), L.Get("trend_score"), true),
        };
        foreach (var (s, title, higherIsBetter) in series) TrendGrid.Children.Add(TrendCard(s, title, higherIsBetter));

        var (recent, previous) = Trends.WinRate(all);
        var wr = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        wr.Children.Add(Ui.Text(L.Get("trend_wr"), "Eyebrow"));
        var wrValue = Ui.Text("—", "BigValue");
        if (recent is double r) Motion.CountUp(wrValue, r * 100, "0", "%");
        wr.Children.Add(wrValue);
        wr.Children.Add(Ui.Text(previous is double p && recent is double r2
            ? L.Format("trend_wr_change", p.ToString("P0", CultureInfo.InvariantCulture), Signed((r2 - p) * 100, "0") + " п.п.")
            : L.Get("trend_wr_few"), "MutedText"));
        TrendGrid.Children.Add(wr);
        // оценки матчей без оценки досчитываются в фоне один раз за запуск; сохранение само обновит вкладку
        _ = App.Host.BackfillScoresAsync();
    }

    private static string Signed(double v, string format) => (v > 0 ? "+" : "") + v.ToString(format, CultureInfo.InvariantCulture);

    private FrameworkElement TrendCard(TrendSeries s, string title, bool higherIsBetter)
    {
        var L = Loc.Instance;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        panel.Children.Add(Ui.Text(title, "Eyebrow"));
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
        MatchMeta.Text = $"{m.StartedUtc.ToLocalTime():g} · {role} · " + (m.Team is string team ? char.ToUpperInvariant(team[0]) + team[1..] + " · " : "") +
                         L.Get(m.FromGsi ? "hist_source_gsi" : "hist_source_od") +
                         (m.MatchId != null ? $" · ID {m.MatchId}" : "") +
                         (m.Partial ? " · " + L.Get("hist_partial") : "");
        MatchMeta.ToolTip = m.Partial ? L.Get("hist_partial_tip") : null;

        TakeawaysPanel.Children.Clear();
        if (m.DurationSec >= PostMatch.MinDurationSec)
        {
            var summary = PostMatch.Build(m, _all, k => App.Host.Database.FindItem(k)?.Name);
            TakeawaysPanel.Children.Add(new MatchTakeaways(summary));
        }
        SummaryChips.Children.Clear();
        SummaryChips.Children.Add(Ui.Chip(Ui.Up(ResultText(m.Result)), m.Result == MatchResult.Win ? "AccentSoft" : "Panel3",
            m.Result == MatchResult.Win ? "Accent" : m.Result == MatchResult.Loss ? "Red" : "Muted"));
        SummaryChips.Children.Add(Ui.Chip(L.Format("hist_duration", GameTimers.Clock(m.DurationSec))));
        SummaryChips.Children.Add(Ui.Chip($"K/D/A {m.Kills}/{m.Deaths}/{m.Assists}"));
        // ноль здесь значит «нет данных» (запись без графика), а не «0 золота» — показываем прочерк
        static string N(int v) => v > 0 ? v.ToString() : "—";
        SummaryChips.Children.Add(Ui.Chip($"GPM {N(m.Gpm)} · XPM {N(m.Xpm)}"));
        SummaryChips.Children.Add(Ui.Chip($"LH {N(m.LastHits)}" + (m.Denies > 0 ? $" / DN {m.Denies}" : "")));
        if (m.Level > 0) SummaryChips.Children.Add(Ui.Chip(L.Format("hist_level", m.Level)));
        ShowOdReview(m);
        ShowBreakdown(m);

        DrawChart();

        ItemsPanel.Children.Clear();
        var history = _all;
        int i = 1;
        foreach (var (key, at) in m.ItemTimings.Where(x => !MatchRecorder.IsRecipe(x.Key)).OrderBy(x => x.Value))
        {
            var item = App.Host.Database.FindItem(key);
            string name = item?.Name ?? Pretty(key);
            bool approx = m.IsApproxTiming(key);
            var bench = approx ? null : MatchStats.Benchmark(history, m, key);
            string detail = approx ? L.Get("hist_item_approx")
                : bench == null ? L.Get("hist_bench_none")
                : L.Format(bench.SameHero ? "hist_bench_hero" : "hist_bench_role", GameTimers.Clock(bench.AvgSec), bench.Matches)
                  + " · " + Delta(at - bench.AvgSec);
            ItemsPanel.Children.Add(Ui.Numbered(i++, name, detail, (approx ? "≤ " : "") + GameTimers.Clock(at), Ui.ItemIcon(key)));
        }
        if (i == 1) ItemsPanel.Children.Add(Ui.State(UiState.Empty, L.Get(m.FromGsi ? "hist_items_none" : "hist_items_od"), ""));

        ShowDeaths(m);

        AlertsPanel.Children.Clear();
        if (!m.FromGsi) AlertsPanel.Children.Add(Ui.Text(L.Get("hist_alerts_od"), "MutedText"));
        else if (m.Alerts.Count == 0) AlertsPanel.Children.Add(Ui.Text(L.Get("hist_alerts_none"), "MutedText"));
        else
            foreach (var (key, count) in m.Alerts.OrderByDescending(x => x.Value))
                AlertsPanel.Children.Add(Ui.Row(AlertName(key),
                    m.AlertsFollowed.TryGetValue(key, out var done) && done > 0 ? L.Format("alerts_followed", done) : "", "×" + count, "Warning"));
    }

    private static string AlertName(string key) => Loc.T(key + "_name");

    // ------------------------------------------------------------------ 0.17: ошибки, сравнение, фазы

    private static string Clock(int s) => $"{s / 60}:{s % 60:00}";

    /// <summary>Матч по фазам: смерти и золото под риском, добивания и GPM против обычного, поздние предметы.</summary>
    private void ShowBreakdown(MatchRecord m)
    {
        var L = Loc.Instance;
        BreakdownPanel.Children.Clear();
        var bd = PlayerReport.Breakdown(m, _all);
        if (bd == null) { BreakdownPanel.Children.Add(Ui.Text(L.Get("bd_none"), "MutedText")); return; }
        BreakdownPanel.Children.Add(new TextBlock
        {
            Text = bd.ComparisonBasis is string basis ? L.Format("bd_hint_basis", L.Get(basis), bd.Compared) : L.Get("bd_no_baseline"), Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        foreach (var p in bd.Phases)
        {
            if (!p.Covered)
            {
                // отрезок записан не полностью — не говорим «смертей не было» и не сравниваем
                var gap = new StackPanel();
                var gapTitle = Ui.Text(p.Text(L.Language), "Value");
                gapTitle.FontWeight = FontWeights.SemiBold;
                gap.Children.Add(gapTitle);
                gap.Children.Add(new TextBlock { Text = L.Get("bd_no_data"), Style = (Style)FindResource("MutedText"), ToolTip = L.Get("hist_partial_tip") });
                BreakdownPanel.Children.Add(new Border { Style = (Style)FindResource("Callout"), Margin = new Thickness(0, 0, 0, 8), BorderBrush = Ui.Res("LineStrong"), Child = gap });
                continue;
            }
            var parts = new List<string> { L.Format("bd_deaths", p.Deaths) };
            if (p.GoldLost > 0) parts.Add(L.Format("bd_gold", p.GoldLost));
            if (p.LastHits is int lh) parts.Add(L.Format("bd_lh", lh) + (p.LastHitsUsual is int ul ? L.Format("bd_usual", ul) : ""));
            if (p.Gpm is int g) parts.Add(L.Format("bd_gpm", g) + (p.GpmUsual is int ug ? L.Format("bd_usual", ug) : ""));
            bool bad = p.Deaths >= 3 || (p.LastHits is int a && p.LastHitsUsual is int b && a < b * 0.85)
                       || (p.Gpm is int c && p.GpmUsual is int d && c < d * 0.85);
            bool good = !bad && p.Deaths == 0 && p.Gpm is int e && p.GpmUsual is int f && e >= f;
            var panel = new StackPanel();
            var title = Ui.Text(p.Text(L.Language), "Value");
            title.FontWeight = FontWeights.SemiBold;
            panel.Children.Add(title);
            panel.Children.Add(new TextBlock { Text = string.Join(" · ", parts), Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap });
            BreakdownPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("Callout"), Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = Ui.Res(bad ? "Warning" : good ? "Success" : "LineStrong"), Child = panel
            });
        }
        if (bd.LateItems.Count > 0)
        {
            BreakdownPanel.Children.Add(new TextBlock { Text = L.Get("bd_late"), FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Text"), Margin = new Thickness(0, 4, 0, 4) });
            foreach (var it in bd.LateItems)
            {
                var name = App.Host.Database.FindItem(it.ItemKey)?.DisplayName(L.Language) ?? it.ItemKey.Replace("item_", "");
                var line = L.Format("bd_late_line", name, Clock(it.Yours), Clock(it.Usual), (int)Math.Round(it.Delta / 60.0));
                var row = Ui.WithImage(Ui.ItemIcon(it.ItemKey, 40), Ui.Text(line, "Value"));
                row.Margin = new Thickness(0, 0, 0, 6);
                BreakdownPanel.Children.Add(row);
            }
        }
    }

    /// <summary>Топ повторяющихся ошибок за 20 игр: доля, тренд и примеры (клик по примеру открывает матч).</summary>
    private void ShowRecurring(IReadOnlyList<MatchRecord> all)
    {
        var L = Loc.Instance;
        RecurringPanel.Children.Clear();
        var list = PlayerReport.Recurring(all);
        if (list.Count == 0) { RecurringPanel.Children.Add(new TextBlock { Text = L.Get("rec_none"), Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap }); return; }
        foreach (var r in list)
        {
            var panel = new StackPanel();
            var title = Ui.Text(r.Text(L.Language), "Value");
            title.FontWeight = FontWeights.SemiBold;
            panel.Children.Add(title);
            var trendKey = r.Trend < 0 ? "rec_better" : r.Trend > 0 ? "rec_worse" : "rec_same";
            var meta = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            meta.Inlines.Add(new System.Windows.Documents.Run(L.Format("rec_line", r.Count, r.Of, (int)Math.Round(r.Rate * 100)) + " · ") { Foreground = Ui.Res("Muted") });
            meta.Inlines.Add(new System.Windows.Documents.Run(L.Get(trendKey)) { Foreground = Ui.Res(r.Trend < 0 ? "Success" : r.Trend > 0 ? "Warning" : "Muted") });
            panel.Children.Add(meta);
            var examples = new WrapPanel();
            foreach (var m in r.Examples)
            {
                var b = new Button
                {
                    Content = $"{HeroName(m)} · {m.StartedUtc.ToLocalTime():dd.MM}", Style = (Style)FindResource("GhostButton"),
                    Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 3, 8, 3)
                };
                var id = m.Id;
                b.Click += (_, _) => { PendingSelectId = id; TabMatches.IsChecked = true; Refresh(); };
                examples.Children.Add(b);
            }
            panel.Children.Add(examples);
            RecurringPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("Callout"), BorderBrush = Ui.Res(r.Trend > 0 ? "Warning" : r.Trend < 0 ? "Success" : "LineStrong"), Child = panel
            });
        }
    }

    private bool _fillingCompare;

    private void FillCompareHeroes()
    {
        var L = Loc.Instance;
        _fillingCompare = true;
        var keep = Ui.TryValue<int>(CompareHero, out var was) ? was : 0;
        var reviews = App.Host.Reviews.Recent(200);
        CompareHero.Items.Clear();
        CompareHero.Items.Add(new Choice<int>(0, L.Get("cmp_all_heroes")));
        foreach (var g in reviews.GroupBy(r => r.HeroId).OrderByDescending(g => g.Count()))
        {
            var hero = App.Host.Database.FindHero(g.Key);
            if (hero == null) continue;
            CompareHero.Items.Add(new Choice<int>(g.Key, $"{hero.DisplayName(L.Language)} ({g.Count()})") { Icon = Roshiq.App.Services.HeroArt.Portrait(hero.Key) });
        }
        CompareHero.SelectedItem = CompareHero.Items.OfType<Choice<int>>().FirstOrDefault(c => c.Value == keep) ?? CompareHero.Items[0];
        _fillingCompare = false;
        ShowCompare();
    }

    private void CompareHero_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_fillingCompare) ShowCompare();
    }

    /// <summary>Процентили OpenDota по разобранным матчам: полоса 0–100% и пометки сильной / слабой стороны.</summary>
    private void ShowCompare()
    {
        var L = Loc.Instance;
        ComparePanel.Children.Clear();
        int? heroId = Ui.TryValue<int>(CompareHero, out var id) && id > 0 ? id : null;
        var rows = PlayerReport.Percentiles(App.Host.Reviews.Recent(200), heroId);
        if (rows.Count == 0) { ComparePanel.Children.Add(new TextBlock { Text = L.Get("cmp_none"), Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap }); return; }
        var best = rows.MaxBy(r => r.AvgPct);
        var worst = rows.MinBy(r => r.AvgPct);
        foreach (var r in rows)
        {
            int pct = (int)Math.Round(r.AvgPct * 100);
            var head = new DockPanel();
            var value = new TextBlock { Text = pct + "%", FontWeight = FontWeights.SemiBold, Foreground = Ui.Res(pct >= 60 ? "Success" : pct < 40 ? "Warning" : "Text") };
            DockPanel.SetDock(value, Dock.Right);
            head.Children.Add(value);
            var label = r.Text(L.Language);
            if (rows.Count >= 3 && ReferenceEquals(r, best) && pct >= 50) label += " · " + L.Get("cmp_strong");
            else if (rows.Count >= 3 && ReferenceEquals(r, worst) && pct < 50) label += " · " + L.Get("cmp_weak");
            head.Children.Add(new TextBlock { Text = label, Foreground = Ui.Res("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 4, 0, 2) };
            bar.Children.Add(new Border { Background = Ui.Res("Panel3"), CornerRadius = new CornerRadius(3) });
            bar.Children.Add(new Border
            {
                Background = Ui.Res(pct >= 60 ? "Success" : pct < 40 ? "Warning" : "Accent"), CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Tag = r.AvgPct
            });
            bar.SizeChanged += (s, _) => { var g = (Grid)s; ((Border)g.Children[1]).Width = g.ActualWidth * Math.Clamp((double)((Border)g.Children[1]).Tag, 0, 1); };
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            block.Children.Add(head);
            block.Children.Add(bar);
            block.Children.Add(new TextBlock { Text = L.Format("cmp_matches", r.Matches), Style = (Style)FindResource("MutedText"), FontSize = 11.5 });
            ComparePanel.Children.Add(block);
        }
    }

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
        if (m.UnlocatedDeaths > 0) DeathsPanel.Children.Add(Ui.Text(L.Format("deaths_unlocated", m.UnlocatedDeaths), "MutedText", 4));
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

    private async void ById_Click(object sender, RoutedEventArgs e)
    {
        var L = Loc.Instance;
        if (MatchIds.Parse(MatchIdBox.Text) is not long id) { ByIdStatus.Text = L.Get("byid_invalid"); return; }
        ByIdButton.IsEnabled = false;
        ByIdStatus.Text = L.Get("review_loading");
        try
        {
            var (recordId, message) = await App.Host.AnalyzeMatchByIdAsync(id);
            ByIdStatus.Text = message;
            if (recordId != null)
            {
                // показать матч с разбором во вкладке «Матчи»
                PendingSelectId = recordId;
                _listKey = "";
                TabMatches.IsChecked = true;
                Refresh();
            }
        }
        finally { ByIdButton.IsEnabled = true; }
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
            var result = App.Host.History.Import(rows);
            ImportStatus.Text = rows.Count == 0 ? L.Get("hist_import_empty")
                : result.Failed > 0 ? L.Format("hist_import_done_failed", result.Added, result.Updated, result.Failed, rows.Count)
                : L.Format("hist_import_done2", result.Added, result.Updated, rows.Count);
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
