using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Roshiq.App.Services;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

/// <summary>Итог сразу после матча: результат, что получилось и одна главная ошибка; «Подробнее» — история.</summary>
public partial class PostMatchWindow : Window
{
    private readonly PostMatchSummary _summary;
    /// <summary>Сколько отложенных советов помещается в окно итога; остальные — в истории матча.</summary>
    public const int MaxDeferredShown = 4;
    /// <summary>Сколько моментов разбора по ходу матча помещается в окно итога; все — в истории матча.</summary>
    public const int MaxStoryShown = 5;

    public PostMatchWindow(PostMatchSummary summary)
    {
        InitializeComponent();
        _summary = summary;
        var L = Loc.Instance;
        var m = summary.Match;
        var hero = App.Host.Database.FindHero(m.HeroId) ?? App.Host.Database.FindHero(m.HeroKey);

        ResultText.Text = L.Get(m.Result switch { MatchResult.Win => "pm_win", MatchResult.Loss => "pm_loss", _ => "pm_unknown" }).ToUpperInvariant();
        ResultText.Foreground = Ui.Res(m.Result switch { MatchResult.Win => "Success", MatchResult.Loss => "Red", _ => "Muted" });
        HeroText.Text = hero == null ? "?" : L.Language == Roshiq.Core.Models.Language.English ? hero.Name : hero.NameRu;
        StatsText.Text = $"{m.Kills}/{m.Deaths}/{m.Assists} · {m.DurationSec / 60}:{m.DurationSec % 60:00} · {m.Gpm} GPM · {m.LastHits} LH"
                         + (m.Position is Position p ? " · " + L.PositionName(p) : "");
        Portrait.Source = HeroArt.Portrait(hero?.Key);
        if (Portrait.Source == null) PortraitBox.Visibility = Visibility.Collapsed;

        foreach (var g in summary.Good)
            GoodPanel.Children.Add(Line(g.Text(L.Language)));
        if (summary.Good.Count == 0) GoodPanel.Children.Add(Line(L.Get("pm_good_none"), muted: true));

        if (summary.MainMistake is { } bad) MistakeText.Text = bad.Text(L.Language);
        else MistakeBlock.Visibility = Visibility.Collapsed;

        // разбор по ходу матча: главное (оно уже выше) не повторяем
        var story = summary.Timeline.Where(sp => !ReferenceEquals(sp.Insight, summary.MainMistake) && !summary.Good.Contains(sp.Insight))
            .OrderBy(sp => sp.Insight.Severity).Take(MaxStoryShown).OrderBy(sp => sp.Clock).ToList();
        foreach (var sp in story) StoryPanel.Children.Add(StoryLine(sp, L.Language));
        if (story.Count == 0) StoryBlock.Visibility = Visibility.Collapsed;

        // как сработали подсказки: что помогло, что пропущено; отложенные советы — коротко, остальное в истории
        bool ru = L.Language == Roshiq.Core.Models.Language.Russian;
        var fb = PostMatch.Feedback(m, MaxDeferredShown);
        if (fb.HelpedKey != null)
            FeedbackPanel.Children.Add(Line(L.Format("pm_feedback_followed", PostMatch.AlertName(fb.HelpedKey, ru), fb.HelpedCount)));
        if (fb.MissedKey != null)
            FeedbackPanel.Children.Add(Line(L.Format("pm_feedback_ignored", PostMatch.AlertName(fb.MissedKey, ru), fb.MissedCount), muted: true));
        if (!fb.HasFeedback) FeedbackBlock.Visibility = Visibility.Collapsed;

        foreach (var tip in fb.Deferred)
            DeferredPanel.Children.Add(Line(PostMatch.DeferredLine(tip), muted: true));
        if (fb.DeferredMore > 0) DeferredPanel.Children.Add(Line(L.Format("pm_deferred_more", fb.DeferredMore), muted: true));
        if (fb.Deferred.Count == 0) DeferredBlock.Visibility = Visibility.Collapsed;

        NextGoalText.Text = PostMatch.NextGoal(summary).Text(L.Language);
        Loaded += async (_, _) =>
        {
            // сравнение с игроками на герое: перцентили OpenDota из кэша или одним запросом
            var (rows, score) = await App.Host.CompareMatchAsync(m);
            if (score is int s)
            {
                ScoreValue.Text = s.ToString(CultureInfo.InvariantCulture);
                ScoreVerdict.Text = L.Get(HeroComparison.ScoreVerdictKey(s));
                ScoreRow.Visibility = Visibility.Visible;
            }
            foreach (var b in HeroComparison.ToBenchmarks(rows).OrderBy(b => b.Pct)) BenchPanel.Children.Add(HistoryView.BenchmarkBar(b, L));
            if (rows.Count > 0) BenchBlock.Visibility = Visibility.Visible;
        };
        BasisText.Text = (summary.Compared >= PostMatch.MinValues && summary.Basis is string basis
            ? L.Format("pm_basis2", L.Get(basis), summary.Compared) : L.Get("pm_basis_few"))
            + (summary.RoleMismatch && m.Position is Position rp ? " " + L.Format("pm_role_mismatch", L.PositionName(rp)) : "");
    }

    /// <summary>Строка разбора по ходу матча: время и что произошло; проблемы — тёплым цветом, удачи — зелёным.</summary>
    internal static FrameworkElement StoryLine(StoryPoint p, Roshiq.Core.Models.Language lang)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var clock = new TextBlock
        {
            Text = $"{p.Clock / 60}:{p.Clock % 60:00}", Width = 46, Foreground = Ui.Res(p.Insight.Severity < 0 ? "Warning" : "Success"),
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Top
        };
        if (Application.Current.TryFindResource("Num") is Style num) clock.Style = num;
        DockPanel.SetDock(clock, Dock.Left);
        dock.Children.Add(clock);
        dock.Children.Add(new TextBlock { Text = p.Insight.Text(lang), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Text") });
        return dock;
    }

    private static FrameworkElement Line(string text, bool muted = false)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var icon = new Roshiq.Overlay.Icon
        {
            Glyph = Ui.Glyph("I.Spark"), Size = 14, Brush = Ui.Res(muted ? "Faint" : "Success"),
            Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top
        };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res(muted ? "Muted" : "Text") });
        return dock;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        HistoryView.PendingSelectId = _summary.Match.Id;
        MainWindow.Navigate("history");
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (DontShow.IsChecked == true) App.Host.Settings.Update(s => s.Automation.PostMatchSummary = false);
        base.OnClosed(e);
    }
}
