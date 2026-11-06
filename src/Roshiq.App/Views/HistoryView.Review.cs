using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Overlay;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

/// <summary>История: разбор матча через OpenDota и закономерности по всем играм.</summary>
public partial class HistoryView
{
    private bool _reviewBusy;

    // ------------------------------------------------------------------ разбор одного матча

    private void ShowOdReview(MatchRecord m)
    {
        var L = Loc.Instance;
        OdReviewPanel.Children.Clear();
        if (m.MatchId == null || !m.MatchId.All(char.IsDigit)) return;

        OdReviewPanel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Eyebrow"),
            Text = L.Get("review_title").ToUpperInvariant(),
            Margin = new Thickness(0, 4, 0, 6)
        });
        var review = App.Host.Reviews.Get(m.MatchId);
        if (review == null)
        {
            OdReviewPanel.Children.Add(Ui.Text(App.Host.SteamAccount == null ? L.Get("review_need_steam") : L.Get("review_none"), "MutedText"));
            if (App.Host.SteamAccount != null) OdReviewPanel.Children.Add(ReviewButton(m, L.Get("review_run")));
            return;
        }

        // главные выводы
        foreach (var ins in review.Top)
            OdReviewPanel.Children.Add(InsightLine(ins, L));
        if (review.Insights.Count == 0)
            OdReviewPanel.Children.Add(Ui.Text(L.Get(review.Parsed ? "review_no_insights" : "review_not_parsed"), "MutedText"));

        // сравнение с игроками на герое
        if (review.Benchmarks.Count > 0)
        {
            OdReviewPanel.Children.Add(SubTitle(L.Get("review_bench")));
            foreach (var b in review.Benchmarks.OrderBy(b => b.Pct))
                OdReviewPanel.Children.Add(BenchmarkBar(b, L));
        }

        // линия
        if (review.LastHits10 != null || review.LaneEfficiency != null)
        {
            OdReviewPanel.Children.Add(SubTitle(L.Get("review_lane")));
            var parts = new List<string>();
            if (review.LastHits10 is int lh) parts.Add(L.Format("review_lane_lh", lh, review.Denies10 ?? 0));
            if (review.LastHits10Target is int t) parts.Add(L.Format("review_lane_target", t));
            if (review.LaneEfficiency is double eff) parts.Add(L.Format("review_lane_eff", eff.ToString("0")));
            OdReviewPanel.Children.Add(Ui.Text(string.Join(" · ", parts)));
        }

        // тайминги предметов
        if (review.Items.Count > 0)
        {
            OdReviewPanel.Children.Add(SubTitle(L.Get("review_items")));
            int i = 1;
            foreach (var it in review.Items)
            {
                var name = App.Host.Database.FindItem(it.ItemKey)?.Name ?? it.ItemKey;
                string detail = it.Norm is int n
                    ? L.Format("review_item_norm", GameTimers.Clock(n)) + " · " + Delta(it.Yours - n)
                    : L.Get("review_item_no_norm");
                OdReviewPanel.Children.Add(Ui.Numbered(i++, name, detail, GameTimers.Clock(it.Yours), Ui.ItemIcon(it.ItemKey)));
            }
        }

        // кто убивал
        if (review.Killers.Count > 0)
        {
            OdReviewPanel.Children.Add(SubTitle(L.Get("review_killers")));
            var wrap = new WrapPanel();
            foreach (var k in review.Killers.Take(5))
            {
                var hero = App.Host.Database.FindHero(k.HeroId);
                var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
                if (hero != null) stack.Children.Add(Ui.Portrait(hero, 44));
                stack.Children.Add(new TextBlock
                {
                    Text = (hero?.Name ?? "?") + " ×" + k.Count, Style = (Style)FindResource("Value"),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
                });
                wrap.Children.Add(stack);
            }
            OdReviewPanel.Children.Add(wrap);
        }

        // варды
        if (review.Observers.Count > 0)
        {
            OdReviewPanel.Children.Add(SubTitle(L.Get("review_wards")));
            var points = review.Observers.Select(w => new MiniMap.Point2(w.X, w.Y,
                w.Dewarded ? Ui.Res("Red") : w.Lifetime is int life && life < 200 ? Ui.Res("Warning") : Ui.Res("Accent"),
                GameTimers.Clock(w.Placed) + (w.Lifetime is int l ? " · " + GameTimers.Clock(l) : "") + (w.Dewarded ? " · " + L.Get("review_dewarded") : "")));
            OdReviewPanel.Children.Add(MiniMap.Create(points, 220));
            int dewarded = review.Observers.Count(w => w.Dewarded);
            OdReviewPanel.Children.Add(Ui.Text(L.Format("review_wards_line", review.Observers.Count, review.Sentries, dewarded,
                review.ObserverAvgLife is double avg ? GameTimers.Clock((int)avg) : "—"), "MutedText", 6));
        }

        OdReviewPanel.Children.Add(ReviewButton(m, L.Get("review_refresh")));
    }

    private FrameworkElement ReviewButton(MatchRecord m, string text)
    {
        var status = new TextBlock { Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        var button = new Button
        {
            Content = text.ToUpperInvariant(), Style = (Style)FindResource("GhostButton"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), IsEnabled = !_reviewBusy
        };
        button.Click += async (_, _) =>
        {
            _reviewBusy = true;
            button.IsEnabled = false;
            status.Text = Loc.T("review_loading");
            try
            {
                var (review, message) = await App.Host.ReviewMatchAsync(m.MatchId!, m.Position, requestParse: true);
                if (review != null && _selected?.Id == m.Id) { ShowMatch(m); return; }
                status.Text = message ?? "";
            }
            finally
            {
                _reviewBusy = false;
                button.IsEnabled = true;
            }
        };
        var panel = new StackPanel();
        panel.Children.Add(button);
        panel.Children.Add(status);
        return panel;
    }

    private TextBlock SubTitle(string text) => new()
    {
        Text = text, Style = (Style)FindResource("MutedText"), FontWeight = FontWeights.SemiBold,
        Foreground = Ui.Res("Text"), Margin = new Thickness(0, 12, 0, 4)
    };

    private static FrameworkElement InsightLine(Insight ins, Loc L)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var icon = new Icon
        {
            Glyph = Ui.Glyph(ins.Severity < 0 ? "I.Alert" : "I.Spark"), Size = 15,
            Brush = Ui.Res(ins.Severity <= -2 ? "Red" : ins.Severity < 0 ? "Warning" : "Accent"),
            Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top
        };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(new TextBlock { Text = ins.Text(L.Language), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Text") });
        return dock;
    }

    /// <summary>Полоска «лучше, чем у N% игроков на этом герое».</summary>
    private FrameworkElement BenchmarkBar(Roshiq.Data.Benchmark b, Loc L)
    {
        var (ru, en) = MatchReviewBuilder.BenchmarkNames[b.Key];
        int pct = (int)Math.Round(b.Pct * 100);
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.Children.Add(new TextBlock { Text = L.Language == Roshiq.Core.Models.Language.English ? en : ru, Foreground = Ui.Res("Muted"), VerticalAlignment = VerticalAlignment.Center });
        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Ui.Res("Panel3"), VerticalAlignment = VerticalAlignment.Center };
        var fill = new Border
        {
            Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Ui.Res(b.Pct < 0.3 ? "Red" : b.Pct < 0.5 ? "Warning" : "Accent")
        };
        track.Child = fill;
        track.SizeChanged += (_, e) => fill.Width = Math.Max(4, e.NewSize.Width * b.Pct);
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);
        var label = new TextBlock
        {
            Text = L.Format("review_bench_pct", pct), Foreground = Ui.Res("Text"), Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);
        grid.ToolTip = b.Raw.ToString(b.Raw >= 100 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture);
        return grid;
    }

    // ------------------------------------------------------------------ закономерности, матчапы, карта смертей

    private void ShowPatterns(IReadOnlyList<MatchRecord> all)
    {
        var L = Loc.Instance;
        PatternsPanel.Children.Clear();
        var insights = Patterns.Insights(all);
        foreach (var ins in insights.Take(4)) PatternsPanel.Children.Add(InsightLine(ins, L));
        if (insights.Count == 0)
            PatternsPanel.Children.Add(Ui.Text(L.Get(all.Count(m => m.Result != MatchResult.Unknown) < 10 ? "pattern_need_more" : "pattern_none"), "MutedText"));
        PatternsPanel.Children.Add(SubTitle(L.Get("pattern_by_duration")));
        foreach (var b in Patterns.ByDuration(all).Where(b => b.Games > 0))
            PatternsPanel.Children.Add(Ui.Row(L.Language == Roshiq.Core.Models.Language.English ? b.En : b.Ru, L.Format("pattern_games", b.Games), b.WinRate.ToString("P0"),
                b.WinRate >= 0.5 ? "Accent" : "Warning"));

        MatchupsPanel.Children.Clear();
        if (App.Host.SteamAccount == null)
            MatchupsPanel.Children.Add(Ui.Text(L.Get("review_need_steam"), "MutedText"));
        else
        {
            var worst = App.Host.PlayerHeroes.WorstMatchups();
            if (worst.Count == 0) MatchupsPanel.Children.Add(Ui.Text(L.Get(App.Host.PlayerHeroes.Count == 0 ? "matchups_no_data" : "matchups_none"), "MutedText"));
            foreach (var w in worst)
            {
                var hero = App.Host.Database.FindHero(w.HeroId);
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                if (hero != null)
                {
                    var pic = Ui.Portrait(hero, 44);
                    pic.Margin = new Thickness(0, 0, 10, 0);
                    DockPanel.SetDock(pic, Dock.Left);
                    row.Children.Add(pic);
                }
                var badge = new TextBlock { Text = w.AgainstWinRate.ToString("P0"), Foreground = Ui.Res("Red"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(badge, Dock.Right);
                row.Children.Add(badge);
                row.Children.Add(new TextBlock
                {
                    Text = (hero?.Name ?? "?") + " · " + L.Format("pattern_games", w.AgainstGames), Foreground = Ui.Res("Text"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                MatchupsPanel.Children.Add(row);
            }
        }

        DeathMapPanel.Children.Clear();
        var deaths = all.Where(m => m.FromGsi).Take(50)
            .SelectMany(m => m.DeathEvents.Where(d => d.X != null && d.Y != null).Select(d => (m, d))).ToList();
        if (deaths.Count == 0)
        {
            DeathMapPanel.Children.Add(Ui.Text(L.Get("deathmap_none"), "MutedText"));
            return;
        }
        var points = deaths.Select(x =>
        {
            var (px, py) = MiniMap.FromWorld(x.d.X!.Value, x.d.Y!.Value);
            return new MiniMap.Point2(px, py, Ui.Res(x.d.Clock < 600 ? "Warning" : x.d.Clock < 1800 ? "Red" : "Purple"),
                GameTimers.Clock(x.d.Clock) + " · " + (App.Host.Database.FindHero(x.m.HeroId)?.Name ?? ""));
        });
        DeathMapPanel.Children.Add(MiniMap.Create(points, 260));
        DeathMapPanel.Children.Add(Ui.Text(L.Format("deathmap_line", deaths.Count, deaths.Select(x => x.m.Id).Distinct().Count()), "MutedText", 6));
    }
}
