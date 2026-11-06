using System.Windows;
using System.Windows.Controls;
using Roshiq.Core.Compliance;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;

namespace Roshiq.App.Views;

/// <summary>04 / Помощник: подсказки по роли, игровые ситуации (ситуационные предметы), напоминания саппортам.</summary>
public partial class AssistantView : UserControl, IRefreshable
{
    private bool _ready;

    public AssistantView()
    {
        InitializeComponent();
        Loc.Instance.PropertyChanged += (_, _) => { if (IsLoaded) Refresh(); };
        App.Host.StateChanged += (_, _) => { if (IsLoaded) ShowSituations(); };
        Roshiq.App.Services.HeroArt.ItemIconsUpdated += (_, _) => { if (IsLoaded) ShowSituations(); };
    }

    public void Refresh()
    {
        _ready = false;
        PositionBox.Items.Clear();
        foreach (var c in Ui.Positions()) PositionBox.Items.Add(c);
        Ui.SelectValue(PositionBox, App.Host.Settings.Current.Position);
        _ready = true;
        ShowRole();
        ShowSituations();
        Reminders.Refresh();
        MatchTools.Refresh();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (RoleSection == null || Reminders == null || MatchTools == null) return; // вызов из InitializeComponent
        RoleSection.Visibility = TabRole.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Reminders.Visibility = TabReminders.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MatchTools.Visibility = TabMatch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FrameworkElement shown = TabRole.IsChecked == true ? RoleSection : TabReminders.IsChecked == true ? (FrameworkElement)Reminders : MatchTools;
        Motion.Enter(shown, 0, 12);
    }

    private void Position_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !Ui.TryValue<Position>(PositionBox, out var p)) return;
        App.Host.Settings.Update(s => s.Position = p);
        ShowRole();
        ShowSituations();
        Reminders.Refresh();
    }

    private void ShowRole()
    {
        var L = Loc.Instance;
        var pos = App.Host.Settings.Current.Position;
        RoleTitle.Text = Ui.Up(L.PositionName(pos));
        RoleTips.Children.Clear();
        int i = 1;
        var hero = App.Host.CurrentHero;
        if (hero != null)
            foreach (var t in App.Host.Advisor.HeroTips(hero, pos))
                RoleTips.Children.Add(Ui.Numbered(i++, t.Text(L.Language), "", hero.DisplayName(L.Language)));
        foreach (var t in App.Host.Advisor.TipsFor(pos))
            RoleTips.Children.Add(Ui.Numbered(i++, t.Text(L.Language), "", L.Get("tipcat_" + t.Category)));
    }

    /// <summary>Ситуационные предметы текущего героя: «если у противника …» (или реальные враги в тренировке).</summary>
    private void ShowSituations()
    {
        var L = Loc.Instance;
        Situations.Children.Clear();
        var hero = App.Host.CurrentHero;
        var build = App.Host.CurrentBuild;
        if (hero == null || build == null)
        {
            SituationTitle.Text = "—";
            SituationNote.Text = "";
            Situations.Children.Add(Ui.State(UiState.Empty, L.Get("state_no_hero_title"), L.Get("state_no_hero_text")));
            return;
        }
        SituationTitle.Text = Ui.Up(hero.DisplayName(L.Language));
        bool enemyAware = ComplianceGate.IsAllowed(Feature.EnemyAwareItemAdvice, App.Host.Settings.Current.UsageContext) && build.EnemyAware;
        SituationNote.Text = L.Get(enemyAware ? "asst_situations_live" : "asst_situations_ref");
        int i = 1;
        foreach (var p in build[BuildPhase.Situational])
            Situations.Children.Add(Ui.Numbered(i++, p.Item.DisplayName(L.Language), p.Explain(L.Language), image: Ui.ItemIcon(p.Item.Key)));
        if (i == 1) Situations.Children.Add(Ui.State(UiState.Empty, L.Get("state_build_empty_title"), ""));
    }
}
