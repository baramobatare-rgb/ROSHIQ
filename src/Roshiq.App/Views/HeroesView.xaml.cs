using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

public partial class HeroesView : UserControl, IRefreshable
{
    private bool _ready;
    private Hero? _selected;
    private bool _loadingBuilds;
    private string? _previewBuildId;
    private string? _buildContext;
    private PersonalBuild? PreviewBuild => App.Host.Settings.Current.PersonalBuilds.FirstOrDefault(x =>
        x.Id == _previewBuildId && x.HeroKey == _selected?.Key && x.Position == SelectedPosition);
    private Position SelectedPosition => Ui.TryValue<Position>(BuildPosition, out var position) ? position : App.Host.Settings.Current.Position;

    public HeroesView()
    {
        InitializeComponent();
        Ui.HeroTemplate(HeroList);
        Roshiq.App.Services.HeroArt.ItemIconsUpdated += (_, _) => { if (IsLoaded) ShowDetails(); };
        FillFilters();
        _ready = true;
        Loc.Instance.PropertyChanged += (_, _) => { FillFilters(); Refresh(); };
    }

    private void FillFilters()
    {
        _ready = false;
        var L = Loc.Instance;
        Ui.TryValue<Position?>(PositionFilter, out var pos);
        PositionFilter.Items.Clear();
        PositionFilter.Items.Add(new Choice<Position?>(null, L.Get("heroes_all_positions")));
        foreach (var p in Enum.GetValues<Position>()) PositionFilter.Items.Add(new Choice<Position?>(p, L.PositionName(p)));
        Ui.SelectValue(PositionFilter, pos);
        if (PositionFilter.SelectedIndex < 0) PositionFilter.SelectedIndex = 0;

        Ui.TryValue<PrimaryAttribute?>(AttributeFilter, out var attr);
        AttributeFilter.Items.Clear();
        AttributeFilter.Items.Add(new Choice<PrimaryAttribute?>(null, L.Get("heroes_all_attributes")));
        foreach (var a in Enum.GetValues<PrimaryAttribute>()) AttributeFilter.Items.Add(new Choice<PrimaryAttribute?>(a, L.Get("attr_" + a.ToString().ToLowerInvariant())));
        Ui.SelectValue(AttributeFilter, attr);
        if (AttributeFilter.SelectedIndex < 0) AttributeFilter.SelectedIndex = 0;

        var current = App.Host.Settings.Current.Position;
        BuildPosition.Items.Clear();
        foreach (var c in Ui.Positions()) BuildPosition.Items.Add(c);
        Ui.SelectValue(BuildPosition, current);
        _ready = true;
    }

    public void Refresh()
    {
        var L = Loc.Instance;
        Ui.TryValue<Position?>(PositionFilter, out var pos);
        Ui.TryValue<PrimaryAttribute?>(AttributeFilter, out var attr);
        var favorites = App.Host.Settings.Current.FavoriteHeroKeys;
        var heroes = App.Host.Database.Search(SearchBox.Text, pos, attr)
            .Where(h => FavoritesOnly.IsChecked != true || favorites.Contains(h.Key))
            .OrderBy(h => Ui.AttributeOrder(h.Attribute))
            .ThenByDescending(h => favorites.Contains(h.Key))
            .ThenBy(h => h.DisplayName(L.Language))
            .Select(h => Ui.HeroChoice(h, h, (favorites.Contains(h.Key) ? "★  " : "") + h.DisplayName(L.Language)))
            .ToList();
        var keep = _selected;
        HeroList.ItemsSource = Ui.WithHeaders(heroes);
        CountText.Text = L.Format("heroes_count", heroes.Count);
        EmptyFavorites.Visibility = heroes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (keep != null)
        {
            var again = heroes.FirstOrDefault(c => c.Value.Id == keep.Id);
            if (again != null) HeroList.SelectedItem = again;
        }
        if (HeroList.SelectedItem == null && heroes.Count > 0)
        {
            var cur = App.Host.CurrentHero;
            HeroList.SelectedItem = heroes.FirstOrDefault(c => c.Value.Id == cur?.Id) ?? heroes[0];
        }
        ShowDetails();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready) Refresh();
    }

    private void HeroList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = (HeroList.SelectedItem as Choice<Hero>)?.Value;
        ShowDetails();
    }

    private void BuildPosition_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) ShowDetails();
    }

    private void ShowDetails()
    {
        var L = Loc.Instance;
        var db = App.Host.Database;
        var hero = _selected;
        Details.Visibility = hero == null ? Visibility.Collapsed : Visibility.Visible;
        if (hero == null) return;

        FavoriteButton.Content = L.Get(App.Host.Settings.Current.FavoriteHeroKeys.Contains(hero.Key) ? "my_favorite_remove" : "my_favorite_add");
        FavoriteButton.Foreground = Ui.Res(App.Host.Settings.Current.FavoriteHeroKeys.Contains(hero.Key) ? "Accent" : "Text");
        HeroName.Text = Ui.Up(hero.DisplayName(L.Language));
        if ((PortraitHost.Content as FrameworkElement)?.Tag as string != hero.Key)
        {
            var portrait = Ui.Portrait(hero, 176);
            portrait.Tag = hero.Key;
            PortraitHost.Content = portrait;
            Motion.Enter(portrait, -10, 0);
        }
        HeroMeta.Text = Ui.Up($"{hero.Name} · {L.Get("attr_" + hero.Attribute.ToString().ToLowerInvariant())} · " +
                        string.Join(" / ", hero.Positions.Select(p => "POS " + (int)p)));
        TraitPanel.Children.Clear();
        foreach (var t in hero.Traits.OrderBy(t => t)) TraitPanel.Children.Add(Ui.Chip(db.TraitText(t, L.Language)));
        if (!string.Equals(NoteBox.Tag as string, hero.Key, StringComparison.OrdinalIgnoreCase))
        {
            NoteBox.Tag = hero.Key;
            NoteBox.Text = App.Host.NoteFor(hero) ?? "";
            NoteStatus.Text = "";
        }

        // Сборка (справочная: без учёта противника)
        if (!Ui.TryValue<Position>(BuildPosition, out var position)) position = App.Host.Settings.Current.Position;
        FillPersonalBuilds(hero, position);
        var personal = PreviewBuild;
        var build = PersonalBuildResolver.Resolve(personal, hero, position, db) ?? App.Host.Builds.Build(hero, position);
        BuildNote.Text = personal != null ? L.Get("my_custom_note") : (hero.PlaysPosition(position) ? "" : L.Get("heroes_unusual_position") + " ") +
                         L.Get(build.HasCuratedBase ? "heroes_build_curated" : "heroes_build_generated");

        // Текущая стадия матча — только если это герой, которым игрок играет сейчас (по GSI)
        var gsi = App.Host.LastGsi;
        BuildPhase? now = null;
        if (gsi != null && gsi.IsInProgress && App.Host.CurrentHero?.Id == hero.Id && DateTime.UtcNow - gsi.ReceivedUtc < TimeSpan.FromSeconds(40))
            now = AppHost.PhaseForClock(gsi.ClockTime);

        BuildPanel.Children.Clear();
        int n = 1;
        foreach (var phase in new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late, BuildPhase.Situational, BuildPhase.Alternative })
        {
            var picks = build[phase];
            if (picks.Count == 0) continue;

            var head = new DockPanel { Margin = new Thickness(0, n == 1 ? 8 : 18, 0, 10) };
            if (now == phase)
            {
                var chip = Ui.Chip(Ui.Up(L.Get("build_now")), "AccentSoft", "Accent");
                chip.Margin = new Thickness(0);
                DockPanel.SetDock(chip, Dock.Right);
                head.Children.Add(chip);
            }
            var eyebrow = Ui.Eyebrow(n++, L.PhaseName(phase));
            eyebrow.Margin = new Thickness(0);
            eyebrow.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(eyebrow);
            BuildPanel.Children.Add(head);

            var grid = new UniformGrid { Columns = 2 };
            foreach (var p in picks)
            {
                var card = new Border
                {
                    Style = (Style)FindResource("InnerCard"),
                    Margin = new Thickness(0, 0, 10, 10),
                    BorderBrush = Ui.Res(now == phase ? "LineStrong" : "Line")
                };
                var itemRow = Ui.Row(p.Item.DisplayName(L.Language), p.Explain(L.Language), p.Curated ? L.Get("build_base") : null);
                itemRow.Margin = new Thickness(0);
                card.Child = Ui.WithImage(Ui.ItemIcon(p.Item.Key), itemRow);
                grid.Children.Add(card);
            }
            BuildPanel.Children.Add(grid);
        }
        if (n == 1) BuildPanel.Children.Add(Ui.State(UiState.Empty, L.Get("state_build_empty_title"), L.Get("state_build_empty_text")));

        // Контрпики (справочно, по взаимодействию способностей и при наличии — реальной статистике)
        CounterPanel.Children.Clear();
        int ci = 1;
        foreach (var (h, score, reasons) in App.Host.Analyzer.CountersOf(hero, 6))
        {
            var detail = string.Join("; ", reasons.Take(2).Select(r => r.Text(L.Language)));
            CounterPanel.Children.Add(Ui.Numbered(ci++, h.DisplayName(L.Language), detail,
                "POS " + string.Join("/", h.Positions.Select(p => (int)p))));
        }
        if (CounterPanel.Children.Count == 0)
            CounterPanel.Children.Add(Ui.State(UiState.Empty, L.Get("heroes_no_counters"), ""));

        TipsPanel.Children.Clear();
        int ti = 1;
        foreach (var tip in App.Host.Advisor.HeroTips(hero, position))
            TipsPanel.Children.Add(Ui.Numbered(ti++, tip.Text(L.Language), "", tip.Kind == ReasonKind.Risk ? L.Get("tip_warning") : null));
        foreach (var tip in App.Host.Advisor.TipsFor(position).Take(3))
            TipsPanel.Children.Add(Ui.Numbered(ti++, tip.Text(L.Language), "", L.Get("tipcat_" + tip.Category)));
    }

    private void SetCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var position = SelectedPosition;
        var personal = PreviewBuild;
        var key = PersonalBuild.ContextKey(_selected.Key, position);
        App.Host.Settings.Update(s => {
            s.Position = position;
            if (personal == null) s.ActivePersonalBuilds.Remove(key);
            else s.ActivePersonalBuilds[key] = personal.Id;
        });
        App.Host.SelectHero(_selected);
        ShowDetails();
    }

    private void SaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var key = _selected.Key;
        var text = NoteBox.Text.Trim();
        App.Host.Settings.Update(s =>
        {
            if (text.Length == 0) s.HeroNotes.Remove(key);
            else s.HeroNotes[key] = text;
        });
        NoteStatus.Text = Loc.T(text.Length == 0 ? "notes_removed" : "saved");
    }

    private void ClearNote_Click(object sender, RoutedEventArgs e)
    {
        NoteBox.Text = "";
        SaveNote_Click(sender, e);
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var key = _selected.Key;
        App.Host.Settings.Update(s => {
            if (!s.FavoriteHeroKeys.Remove(key)) s.FavoriteHeroKeys.Add(key);
        });
        Refresh();
    }

    private void FillPersonalBuilds(Hero hero, Position position)
    {
        var settings = App.Host.Settings.Current;
        var context = PersonalBuild.ContextKey(hero.Key, position);
        settings.ActivePersonalBuilds.TryGetValue(context, out var activeId);
        if (_buildContext != context) { _buildContext = context; _previewBuildId = activeId; }
        var builds = settings.PersonalBuilds.Where(x => x.HeroKey == hero.Key && x.Position == position).ToList();
        if (!builds.Any(x => x.Id == _previewBuildId)) _previewBuildId = null;
        _loadingBuilds = true;
        PersonalBuildBox.Items.Clear();
        PersonalBuildBox.Items.Add(new Choice<string?>(null, Loc.T("my_reference") + (activeId == null ? " · " + Loc.T("my_active") : "")));
        foreach (var build in builds)
            PersonalBuildBox.Items.Add(new Choice<string?>(build.Id, build.Name + (build.Id == activeId ? " · " + Loc.T("my_active") : "")));
        Ui.SelectValue(PersonalBuildBox, _previewBuildId);
        _loadingBuilds = false;
        EditBuildButton.IsEnabled = DeleteBuildButton.IsEnabled = PreviewBuild != null;
        PersonalBuildNotes.Text = PreviewBuild?.Notes ?? "";
        PersonalBuildNotes.Visibility = string.IsNullOrWhiteSpace(PersonalBuildNotes.Text) ? Visibility.Collapsed : Visibility.Visible;
        PersonalBuildStatus.Text = Loc.T(_previewBuildId == activeId ? "my_active_hint" : "my_preview_hint");
        if (PreviewBuild is { } custom && custom.Entries.Any(x => App.Host.Database.FindItem(x.ItemKey) == null))
            PersonalBuildStatus.Text += " · " + Loc.T("my_missing_item");
    }

    private void PersonalBuild_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingBuilds || !Ui.TryValue<string?>(PersonalBuildBox, out var id)) return;
        _previewBuildId = id;
        ShowDetails();
    }

    private void NewBuild_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var position = SelectedPosition;
        PersonalBuild build;
        if (PreviewBuild is { } personal) build = personal.Copy(newIdentity: true);
        else
        {
            var reference = App.Host.Builds.Build(_selected, position);
            build = new PersonalBuild { HeroKey = _selected.Key, Position = position,
                Name = Loc.T("my_new_name"), Entries = reference.Phases.SelectMany(phase => phase.Value.Select(p =>
                    new PersonalBuildEntry { Phase = phase.Key, ItemKey = p.Item.Key })).ToList() };
        }
        var existing = App.Host.Settings.Current.PersonalBuilds.Where(x => x.HeroKey == _selected.Key && x.Position == position).Select(x => x.Name).ToHashSet();
        var name = build.Name;
        int suffix = 2;
        while (existing.Contains(build.Name)) build.Name = name + " " + suffix++;
        OpenEditor(build);
    }

    private void EditBuild_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewBuild is { } build) OpenEditor(build);
    }
    private void OpenEditor(PersonalBuild build)
    {
        var editor = new PersonalBuildEditor(build);
        if (editor.ShowDialog() != true) return;
        App.Host.Settings.Update(s => {
            int index = s.PersonalBuilds.FindIndex(x => x.Id == editor.Result.Id);
            if (index >= 0) s.PersonalBuilds[index] = editor.Result;
            else s.PersonalBuilds.Add(editor.Result);
        });
        _previewBuildId = editor.Result.Id;
        ShowDetails();
    }
    private void DeleteBuild_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewBuild is not { } build || MessageBox.Show(Application.Current.MainWindow,
            Loc.Instance.Format("my_delete_confirm", build.Name), Loc.T("my_builds"), MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        App.Host.Settings.Update(s => s.PersonalBuilds.RemoveAll(x => x.Id == build.Id));
        _previewBuildId = null;
        ShowDetails();
    }
}
