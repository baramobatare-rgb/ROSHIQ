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
        var m = summary.Match;
        bool ru = L.Language == Roshiq.Core.Models.Language.Russian;
        var fb = PostMatch.Feedback(m); // в истории матча — все отложенные советы, без сокращения
        if (fb.HasFeedback)
        {
            var lines = new List<string>();
            if (fb.HelpedKey != null) lines.Add(L.Format("pm_feedback_followed", PostMatch.AlertName(fb.HelpedKey, ru), fb.HelpedCount));
            if (fb.MissedKey != null) lines.Add(L.Format("pm_feedback_ignored", PostMatch.AlertName(fb.MissedKey, ru), fb.MissedCount));
            Add(L.Get("pm_feedback_title"), string.Join("\n", lines), "Accent");
        }
        if (fb.Deferred.Count > 0)
            Add(L.Get("pm_deferred_title"), string.Join("\n", fb.Deferred.Select(PostMatch.DeferredLine)), "Accent");
        // реальное основание сравнения: какие игры и сколько их; без выборки — честно, что сравнивать не с чем
        Children.Add(Ui.Text(summary.Compared >= PostMatch.MinValues && summary.Basis is string basis
            ? L.Format("pm_basis2", L.Get(basis), summary.Compared) : L.Get("pm_basis_few"), "MutedText", 4));
    }
    private void Add(string title, string body, string color)
    {
        var panel = new StackPanel();
        var heading = Ui.Text(title, "Eyebrow"); heading.Foreground = Ui.Res(color);
        panel.Children.Add(heading); panel.Children.Add(Ui.Text(body));
        // информационный блок: без рамки, цветная полоса слева
        Children.Add(new Border { Style = (Style)Application.Current.FindResource("Callout"), BorderBrush = Ui.Res(color), Child = panel });
    }
}
