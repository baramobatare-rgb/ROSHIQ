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
        // пустые выводы («ошибка не выявлена», общая цель «выберите одну цель») не выглядят так же весомо, как настоящие:
        // настоящие — цветными блоками, пустое — одной бледной строкой
        if (summary.Good.Count > 0)
            Add(L.Get("pm_good"), string.Join("\n", summary.Good.Select(g => g.Text(L.Language))), "Success");
        var goal = PostMatch.NextGoal(summary);
        bool genericGoal = goal.En.StartsWith("Choose one goal before the game", StringComparison.Ordinal);
        if (summary.MainMistake != null)
            Add(L.Get("pm_fix"), summary.MainMistake.Text(L.Language), "Warning");
        else
        {
            var none = Ui.Text(L.Get("pm_no_issue"), "MutedText");
            none.Margin = new Thickness(0, 0, 0, 12);
            Children.Add(none);
        }
        if (!genericGoal)
            Add(L.Get("pm_next_goal"), goal.Text(L.Language), "Accent");
        if (summary.Timeline.Count > 0)
        {
            // весь разбор по ходу матча, по времени
            var panel = new StackPanel();
            var heading = Ui.Text(L.Get("pm_story_title"), "Eyebrow"); heading.Foreground = Ui.Res("Accent");
            panel.Children.Add(heading);
            foreach (var p in summary.Timeline) panel.Children.Add(PostMatchWindow.StoryLine(p, L.Language));
            Children.Add(new Border { Style = (Style)Application.Current.FindResource("Callout"), BorderBrush = Ui.Res("Accent"), Child = panel });
        }
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
        Children.Add(Ui.Text((summary.Compared >= PostMatch.MinValues && summary.Basis is string basis
            ? L.Format("pm_basis2", L.Get(basis), summary.Compared) : L.Get("pm_basis_few"))
            + (summary.RoleMismatch && m.Position is Roshiq.Core.Models.Position rp ? " " + L.Format("pm_role_mismatch", L.PositionName(rp)) : ""), "MutedText", 4));
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
