using System.Windows;
using System.Windows.Controls;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Draft;

namespace Roshiq.App.Views;

/// <summary>Тренажёр драфта вне матча: случайный драфт противника, ваш выбор и разбор от анализатора.</summary>
public partial class DraftTrainerView : UserControl, IRefreshable
{
    private readonly Random _random = new();
    private DraftState? _scenario;
    private bool _answered;
    private int _solved;
    private int _top3;

    public DraftTrainerView()
    {
        InitializeComponent();
        Loc.Instance.PropertyChanged += (_, _) => { if (IsLoaded) Refresh(); };
    }

    public void Refresh()
    {
        UpdateScore();
        if (_scenario != null) ShowScenario();
    }

    private void UpdateScore() =>
        ScoreText.Text = _solved == 0 ? "" : Loc.Instance.Format("trainer_score", _solved, _top3);

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _scenario = App.Host.Trainer.NewScenario(_random, App.Host.Settings.Current.Position);
        _answered = false;
        ResultPanel.Children.Clear();
        ShowScenario();
    }

    private void ShowScenario()
    {
        if (_scenario == null) return;
        var L = Loc.Instance;
        ScenarioPanel.Visibility = Visibility.Visible;
        EnemyChips.Children.Clear();
        foreach (var h in _scenario.Enemies) EnemyChips.Children.Add(HeroTile(h));
        AllyChips.Children.Clear();
        foreach (var h in _scenario.Allies) AllyChips.Children.Add(HeroTile(h));
        if (_scenario.Allies.Count == 0) AllyChips.Children.Add(Ui.Text(L.Get("trainer_no_allies"), "MutedText"));
        RoleText.Text = L.Format("trainer_role", L.PositionName(_scenario.UserPosition));

        var taken = _scenario.Enemies.Concat(_scenario.Allies).Select(h => h.Id).ToHashSet();
        var selected = (PickBox.SelectedItem as Choice<Hero?>)?.Value;
        PickBox.Items.Clear();
        if (PickBox.ItemTemplate == null) Ui.HeroTemplate(PickBox);
        foreach (var c in Ui.WithHeaders(Ui.ByAttribute(App.Host.Database.Heroes.Where(h => !taken.Contains(h.Id)), L.Language)
                     .Select(h => Ui.HeroChoice<Hero?>(h, h, h.DisplayName(L.Language)))))
            PickBox.Items.Add(c);
        if (selected != null && !taken.Contains(selected.Id)) Ui.SelectValue(PickBox, (Hero?)selected);
        AnswerButton.IsEnabled = !_answered;
    }

    /// <summary>Портрет с именем героя для сценария; плитки появляются по очереди.</summary>
    private FrameworkElement HeroTile(Hero h)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 10), Width = 76 };
        sp.Children.Add(Ui.Portrait(h, 76));
        sp.Children.Add(new TextBlock { Text = h.Name, FontSize = 11, Foreground = Ui.Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
        Motion.Enter(sp, 0, 10, (EnemyChips.Children.Count + AllyChips.Children.Count) * 45);
        return sp;
    }

    private void Answer_Click(object sender, RoutedEventArgs e)
    {
        var L = Loc.Instance;
        if (_scenario == null || _answered) return;
        var pick = (PickBox.SelectedItem as Choice<Hero?>)?.Value
                   ?? App.Host.Database.FindHero(PickBox.Text);
        ResultPanel.Children.Clear();
        if (pick == null)
        {
            ResultPanel.Children.Add(Ui.Text(L.Get("trainer_pick_first"), "MutedText"));
            return;
        }
        if (_scenario.Enemies.Concat(_scenario.Allies).Any(h => h.Id == pick.Id))
        {
            ResultPanel.Children.Add(Ui.Text(L.Get("trainer_taken"), "MutedText"));
            return;
        }

        var grade = App.Host.Trainer.Grade(_scenario, pick);
        _answered = true;
        AnswerButton.IsEnabled = false;
        _solved++;
        if (grade.Verdict == TrainerVerdict.Excellent) _top3++;
        UpdateScore();

        var (key, brush) = grade.Verdict switch
        {
            TrainerVerdict.Excellent => ("trainer_excellent", "Accent"),
            TrainerVerdict.Good => ("trainer_good", "Purple"),
            TrainerVerdict.OffRole => ("trainer_offrole", "Warning"),
            _ => ("trainer_weak", "Red")
        };
        ResultPanel.Children.Add(Ui.Row(Ui.Up(L.Get(key)), L.Format("trainer_rank", grade.Rank, grade.Total), pick.Name, brush));
        foreach (var r in grade.PickEvaluation.Positives.Where(r => r.Kind != ReasonKind.RoleFit).Take(4))
            ResultPanel.Children.Add(Ui.Text("+ " + r.Text(L.Language), "MutedText", 2));
        foreach (var r in grade.PickEvaluation.Risks.Take(3))
            ResultPanel.Children.Add(Ui.Text("− " + r.Text(L.Language), "MutedText", 2));

        ResultPanel.Children.Add(Ui.Text(Ui.Up(L.Get("trainer_best")), "Eyebrow", 12));
        int i = 1;
        foreach (var best in grade.Best)
        {
            var why = string.Join("; ", best.Positives.Where(r => r.Kind is ReasonKind.Counter or ReasonKind.Synergy or ReasonKind.TeamGap or ReasonKind.Stats)
                .Take(2).Select(r => r.Text(L.Language)));
            ResultPanel.Children.Add(Ui.Numbered(i++, best.Hero.Name, why, best.Hero.Id == pick.Id ? L.Get("trainer_yours") : null));
        }
    }
}
