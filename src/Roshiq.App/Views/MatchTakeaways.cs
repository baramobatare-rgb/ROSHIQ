using System.Windows;
using System.Windows.Controls;
using Roshiq.Core.Localization;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

public sealed class MatchTakeaways : StackPanel
{
    public MatchTakeaways(PostMatchSummary summary)
    {
        var L = Loc.Instance;
        Add(L.Get("pm_good"), summary.Good.Count > 0 ? string.Join("\n", summary.Good.Select(g => g.Text(L.Language))) : L.Get("pm_good_none"), "Success");
        Add(L.Get("pm_fix"), summary.MainMistake?.Text(L.Language) ?? L.Get("pm_no_issue"), "Warning");
        Add(L.Get("pm_next_goal"), PostMatch.NextGoal(summary).Text(L.Language), "Accent");
        Children.Add(Ui.Text(summary.Compared >= 3 ? L.Format("pm_basis", summary.Compared) : L.Get("pm_basis_few"), "MutedText", 4));
    }
    private void Add(string title, string body, string color)
    {
        var panel = new StackPanel();
        var heading = Ui.Text(title, "Eyebrow"); heading.Foreground = Ui.Res(color);
        panel.Children.Add(heading); panel.Children.Add(Ui.Text(body));
        Children.Add(new Border { Style = (Style)Application.Current.FindResource("InnerCard"), Margin = new Thickness(0, 0, 0, 8), Child = panel });
    }
}
