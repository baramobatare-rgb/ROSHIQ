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

        BasisText.Text = summary.Compared >= 3 ? L.Format("pm_basis", summary.Compared) : L.Get("pm_basis_few");
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
