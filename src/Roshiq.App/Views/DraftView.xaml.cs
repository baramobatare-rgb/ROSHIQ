using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Roshiq.GSI;
using Roshiq.Overlay;
using Roshiq.Core.Compliance;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Draft;
using Roshiq.Vision;

namespace Roshiq.App.Views;

/// <summary>
/// Разбор драфта: ручной ввод (справочник, доступен всегда) и автоматическое распознавание
/// видимых портретов (только Training). Каждую ячейку можно исправить вручную и "запомнить" портрет.
/// </summary>
public partial class DraftView : UserControl, IRefreshable
{
    private sealed class Slot
    {
        public required ComboBox Box { get; init; }
        public required TextBlock Status { get; init; }
        public required Button Teach { get; init; }
        public RgbImage? Crop { get; set; }
    }

    private readonly List<Slot> _allies = new();
    private readonly List<Slot> _enemies = new();
    private bool _ready;
    private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DraftAnalysis? _shownAuto;


    public DraftView()
    {
        InitializeComponent();
        BuildSlots(AllySlots, _allies);
        BuildSlots(EnemySlots, _enemies);
        FillCombos();
        _ready = true;
        _autoTimer.Tick += (_, _) => {
            AutoStatus.Text = App.Host.AutoDraftStatus;
            SideBox.IsEnabled = !App.Host.AutoDraftLive;
            if (App.Host.AutoDraftLive)
            {
                _ready = false;
                Ui.SelectValue(PositionBox, App.Host.Settings.Current.Position);
                Ui.SelectValue(SideBox, App.Host.MySide);
                _ready = true;
            }
            if (App.Host.AutoDraftLive && !ReferenceEquals(_shownAuto, App.Host.AutoDraftAnalysis))
            { _shownAuto = App.Host.AutoDraftAnalysis; ShowAnalysis(_shownAuto); }
        };
        Loaded += (_, _) => { Refresh(); _autoTimer.Start(); };
        Unloaded += (_, _) => _autoTimer.Stop();
        App.Host.Vision.Recognized += (_, r) => Dispatcher.InvokeAsync(() => ApplyRecognition(r));
        App.Host.StateChanged += (_, _) => { if (IsLoaded) UpdateMode(); };
        Loc.Instance.PropertyChanged += (_, _) => { FillCombos(); Refresh(); };
    }

    /// <summary>
    /// Пять слотов-плиток с портретами, как в экране пиков Dota. Щелчок — выбор героя сеткой, × — очистить.
    /// Под плиткой хранится обычный ComboBox (скрытый): на нём держится вся прежняя логика выбора и распознавания.
    /// </summary>
    private void BuildSlots(Panel host, List<Slot> list)
    {
        bool ally = ReferenceEquals(host, AllySlots);
        for (int i = 0; i < 5; i++)
        {
            var box = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, Visibility = Visibility.Collapsed };
            var status = new TextBlock { Foreground = Ui.Res("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
            var teach = new Button { Content = Ui.Up(Loc.T("draft_teach")), Visibility = Visibility.Collapsed, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };

            var portrait = new Border
            {
                CornerRadius = new CornerRadius(8), Height = 74, Background = Ui.Res("Panel2"), BorderBrush = Ui.Res("Line"),
                BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, ClipToBounds = true
            };
            var plus = new TextBlock
            {
                Text = "+", FontSize = 26, FontWeight = FontWeights.Light, Foreground = Ui.Res("Faint"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var clear = new Button
            {
                Content = "×", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(6, 0, 6, 2), Margin = new Thickness(0),
                FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed, ToolTip = Loc.T("picker_clear"), Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0))
            };
            var face = new Grid();
            face.Children.Add(portrait);
            face.Children.Add(plus);
            face.Children.Add(clear);
            var name = new TextBlock
            {
                Text = Loc.T("picker_empty"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Faint"),
                TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0)
            };
            var tile = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 4 ? 0 : 5, 4) };
            tile.Children.Add(face);
            tile.Children.Add(name);
            tile.Children.Add(status);
            tile.Children.Add(teach);
            tile.Children.Add(box);

            var slot = new Slot { Box = box, Status = status, Teach = teach };
            teach.Click += (_, _) => TeachSlot(slot);
            void Paint()
            {
                var hero = (box.SelectedItem as Choice<Hero?>)?.Value;
                var img = hero == null ? null : Roshiq.App.Services.HeroArt.Portrait(hero.Key);
                portrait.Background = img != null ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : Ui.Res("Panel2");
                portrait.BorderBrush = hero == null ? Ui.Res("Line") : Ui.AttributeBrush(hero.Attribute);
                portrait.BorderThickness = new Thickness(hero == null ? 1 : 0, hero == null ? 1 : 0, hero == null ? 1 : 0, hero == null ? 1 : 3);
                plus.Visibility = hero == null ? Visibility.Visible : Visibility.Collapsed;
                clear.Visibility = hero == null ? Visibility.Collapsed : Visibility.Visible;
                name.Text = hero == null ? Loc.T("picker_empty") : hero.DisplayName(Loc.Instance.Language);
                name.Foreground = Ui.Res(hero == null ? "Faint" : "Text");
            }
            box.SelectionChanged += (_, _) =>
            {
                Paint();
                if (_ready) slot.Status.Text = slot.Crop != null ? Loc.T("draft_manual") : "";
            };
            clear.Click += (_, e) => { e.Handled = true; box.SelectedIndex = 0; };
            face.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInside(d, clear)) return;
                var taken = _allies.Concat(_enemies).Where(o => !ReferenceEquals(o, slot))
                    .Select(o => (o.Box.SelectedItem as Choice<Hero?>)?.Value?.Id).Where(id => id != null).Select(id => id!.Value).ToHashSet();
                var picker = new HeroPicker(Loc.T(ally ? "draft_allies" : "draft_enemies") + " · " + (list.IndexOf(slot) + 1), taken,
                    (box.SelectedItem as Choice<Hero?>)?.Value != null,
                    hero => { if (hero == null) box.SelectedIndex = 0; else Ui.SelectValue(box, (Hero?)hero); })
                { Owner = Window.GetWindow(this) };
                picker.Show();
            };
            portrait.MouseEnter += (_, _) => { if ((box.SelectedItem as Choice<Hero?>)?.Value == null) portrait.BorderBrush = Ui.Res("Accent"); };
            portrait.MouseLeave += (_, _) => Paint();
            list.Add(slot);
            host.Children.Add(tile);
        }
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var cur = d; cur != null; cur = cur is Visual ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, container)) return true;
        return false;
    }

    private void FillCombos()
    {
        _ready = false;
        var L = Loc.Instance;
        var pos = App.Host.Settings.Current.Position;
        PositionBox.Items.Clear();
        foreach (var c in Ui.Positions()) PositionBox.Items.Add(c);
        Ui.SelectValue(PositionBox, pos);

        PoolBox.Items.Clear();
        PoolBox.Items.Add(new Choice<PoolMode>(PoolMode.Off, L.Get("pool_off")));
        PoolBox.Items.Add(new Choice<PoolMode>(PoolMode.Prefer, L.Get("pool_prefer")));
        PoolBox.Items.Add(new Choice<PoolMode>(PoolMode.Only, L.Get("pool_only")));
        Ui.SelectValue(PoolBox, App.Host.Settings.Current.PoolMode);

        SideBox.Items.Clear();
        SideBox.Items.Add(new Choice<Side>(Side.Radiant, "Radiant"));
        SideBox.Items.Add(new Choice<Side>(Side.Dire, "Dire"));
        Ui.SelectValue(SideBox, App.Host.MySide);

        var heroes = Ui.ByAttribute(App.Host.Database.Heroes, L.Language)
            .Select(h => Ui.HeroChoice<Hero?>(h, h, h.DisplayName(L.Language)));
        var withHeaders = Ui.WithHeaders(heroes);
        foreach (var slot in _allies.Concat(_enemies))
        {
            if (slot.Box.ItemTemplate == null) Ui.HeroTemplate(slot.Box);
            var selected = (slot.Box.SelectedItem as Choice<Hero?>)?.Value;
            slot.Box.Items.Clear();
            slot.Box.Items.Add(new Choice<Hero?>(null, "—"));
            foreach (var h in withHeaders) slot.Box.Items.Add(h);
            if (selected != null) Ui.SelectValue(slot.Box, (Hero?)selected);
            if (slot.Box.SelectedIndex < 0) slot.Box.SelectedIndex = 0;
            slot.Teach.Content = Ui.Up(L.Get("draft_teach"));
        }
        _ready = true;
    }

    public void Refresh()
    {
        _ready = false;
        AutoDraftBox.IsChecked = App.Host.Settings.Current.Vision.AutoDraftEnabled;
        AutoStatus.Text = App.Host.AutoDraftStatus;
        Ui.SelectValue(PositionBox, App.Host.Settings.Current.Position);
        Ui.SelectValue(SideBox, App.Host.MySide);
        _ready = true;
        UpdateMode();
        ShowMyHero();
        ShowTeam(Selected(_allies));
        ShowAnalysis(App.Host.AutoDraftLive ? App.Host.AutoDraftAnalysis : App.Host.LastDraftAnalysis);
        Trainer.Refresh();
    }

    private void Pool_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !Ui.TryValue<PoolMode>(PoolBox, out var mode)) return;
        App.Host.Settings.Update(s => s.PoolMode = mode);
        if (mode != PoolMode.Off && App.Host.Settings.Current.FavoriteHeroKeys.Count == 0)
        {
            // пул пуст — заполнить по статистике привязанного Steam (часто играете и выигрываете)
            var fromSteam = App.Host.SuggestPoolFromSteam();
            if (fromSteam.Count > 0)
            {
                App.Host.Settings.Update(s => s.FavoriteHeroKeys = fromSteam.ToList());
                ModeNote.Text = Loc.Instance.Format("pool_from_steam", fromSteam.Count);
            }
            else ModeNote.Text = Loc.T("pool_empty");
        }
        var allies = Selected(_allies);
        var enemies = Selected(_enemies);
        if (!App.Host.AutoDraftLive && allies.Count + enemies.Count > 0) App.Host.SetDraft(allies, enemies);
        ShowAnalysis(App.Host.AutoDraftLive ? App.Host.AutoDraftAnalysis : App.Host.LastDraftAnalysis);
    }

    private void AutoDraft_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s => s.Vision.AutoDraftEnabled = AutoDraftBox.IsChecked == true);
        AutoStatus.Text = App.Host.AutoDraftStatus;
    }

    private void SetupAutoDraft_Click(object sender, RoutedEventArgs e)
    {
        var root = SteamLocator.FindDotaRoot();
        if (root == null) { SetupStatus.Text = Loc.T("set_dota_not_found"); return; }
        try
        {
            App.Host.Settings.Update(s => { s.Gsi.Enabled = true; s.Vision.AutoDraftEnabled = true; });
            GsiConfigWriter.Write(root, App.Host.Settings.Current.Gsi.Port, App.Host.Settings.Current.Gsi.Token);
            AutoDraftBox.IsChecked = true;
            SetupStatus.Text = Loc.T("auto_setup_done");
        }
        catch (Exception ex) { SetupStatus.Text = ex.Message; }
    }

    private void ShowMyHero()
    {
        var L = Loc.Instance;
        var hero = App.Host.CurrentHero;
        MyHeroName.Text = hero == null ? "—" : Ui.Up(hero.DisplayName(L.Language));
        MyHeroMeta.Text = hero == null
            ? L.Get("draft_my_hero_none")
            : L.PositionName(App.Host.Settings.Current.Position) + " · " + string.Join(", ", hero.Traits.Take(4).Select(t => App.Host.Database.TraitText(t, L.Language)));
    }

    /// <summary>Оценка состава: какие важные для драфта роли уже закрыты союзниками.</summary>
    private void ShowTeam(IReadOnlyList<Hero> allies)
    {
        var L = Loc.Instance;
        var db = App.Host.Database;
        TeamPanel.Children.Clear();
        var missing = new List<string>();
        foreach (var gap in db.GapRules)
        {
            bool covered = allies.Any(a => a.Has(gap.Trait));
            var label = db.TraitText(gap.Trait, L.Language);
            if (!covered) missing.Add(label);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 8),
                BorderThickness = new Thickness(1),
                BorderBrush = Ui.Res(covered ? "Accent" : "Line"),
                Background = Ui.Res(covered ? "AccentSoft" : "Panel2")
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Icon { Glyph = Ui.Glyph(covered ? "I.Check" : "I.Close"), Size = 13, Brush = Ui.Res(covered ? "Accent" : "Faint"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Foreground = Ui.Res(covered ? "Text" : "Muted"), VerticalAlignment = VerticalAlignment.Center });
            chip.Child = sp;
            TeamPanel.Children.Add(chip);
        }
        TeamNote.Text = allies.Count == 0
            ? L.Get("draft_team_empty")
            : missing.Count == 0 ? L.Get("draft_team_full") : L.Format("draft_team_missing", string.Join(", ", missing));
    }

    private void UpdateMode()
    {
        var ctx = App.Host.Settings.Current.UsageContext;
        bool auto = ComplianceGate.IsAllowed(Feature.AutoDraftRecognition, ctx);
        ScanButton.IsEnabled = auto;
        ModeIcon.Brush = Ui.Res(auto ? "Warning" : "Muted");
        ModeNote.Text = auto
            ? Loc.Instance.Format("draft_mode_training", App.Host.Vision.Matcher.HeroCount)
            : Loc.T("draft_mode_reference");
    }

    private static List<Hero> Selected(IEnumerable<Slot> slots) =>
        slots.Select(s => (s.Box.SelectedItem as Choice<Hero?>)?.Value).Where(h => h != null).Select(h => h!).Distinct().ToList();

    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var allies = Selected(_allies);
        App.Host.SetDraft(allies, Selected(_enemies));
        ShowTeam(allies);
        ShowAnalysis(App.Host.AutoDraftLive ? App.Host.AutoDraftAnalysis : App.Host.LastDraftAnalysis);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        foreach (var s in _allies.Concat(_enemies))
        {
            s.Box.SelectedIndex = 0;
            s.Status.Text = "";
            s.Crop = null;
            s.Teach.Visibility = Visibility.Collapsed;
        }
        _ready = true;
        App.Host.SetDraft(Array.Empty<Hero>(), Array.Empty<Hero>());
        ShowTeam(Array.Empty<Hero>());
        ShowAnalysis(App.Host.AutoDraftLive ? App.Host.AutoDraftAnalysis : App.Host.LastDraftAnalysis);
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Host.Overlay.Game.Found)
        {
            ModeNote.Text = Loc.T("draft_scan_no_game");
            return;
        }
        App.Host.RequestManualScan();
        ModeNote.Text = Loc.T("draft_scanning");
    }

    private void Position_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !Ui.TryValue<Position>(PositionBox, out var p)) return;
        App.Host.Settings.Update(s => s.Position = p);
        ShowMyHero();
        ShowAnalysis(App.Host.AutoDraftLive ? App.Host.AutoDraftAnalysis : App.Host.LastDraftAnalysis);
    }

    private void Side_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !App.Host.AutoDraftLive && Ui.TryValue<Side>(SideBox, out var side)) App.Host.MySide = side;
    }

    private void ApplyRecognition(DraftRecognitionResult result)
    {
        if (result.SessionId != App.Host.AutoDraftGeneration) return;
        var L = Loc.Instance;
        _ready = false;
        foreach (var r in result.Slots)
        {
            var list = r.Region.Side == App.Host.MySide ? _allies : _enemies;
            if (r.Region.Index >= list.Count) continue;
            var slot = list[r.Region.Index];
            slot.Crop = r.Crop;
            switch (r.Match.Status)
            {
                case SlotStatus.Empty:
                    slot.Box.SelectedIndex = 0;
                    slot.Status.Text = L.Get("slot_empty");
                    slot.Teach.Visibility = Visibility.Collapsed;
                    break;
                case SlotStatus.Recognized:
                    var hero = App.Host.Database.FindHero(r.Match.HeroKey);
                    if (hero != null) Ui.SelectValue(slot.Box, (Hero?)hero);
                    slot.Status.Text = L.Format("slot_recognized", r.Match.Score.ToString("0.00"));
                    slot.Teach.Visibility = Visibility.Visible;
                    break;
                case SlotStatus.Ambiguous:
                    var names = string.Join(" / ", r.Match.Candidates.Take(2)
                        .Select(c => App.Host.Database.FindHero(c.HeroKey)?.DisplayName(L.Language) ?? c.HeroKey));
                    slot.Status.Text = L.Format("slot_ambiguous", names);
                    slot.Teach.Visibility = Visibility.Visible;
                    break;
                default:
                    slot.Status.Text = L.Get("slot_unknown");
                    slot.Teach.Visibility = Visibility.Visible;
                    break;
            }
        }
        _ready = true;
        ModeNote.Text = L.Format("draft_scan_done", result.Slots.Count(s => s.Match.Status == SlotStatus.Recognized),
            result.UncertainCount, (int)result.Elapsed.TotalMilliseconds);
        ShowTeam(Selected(_allies));
        ShowAnalysis(App.Host.AutoDraftLive ? App.Host.AutoDraftAnalysis : App.Host.LastDraftAnalysis);
    }

    private void TeachSlot(Slot slot)
    {
        var hero = (slot.Box.SelectedItem as Choice<Hero?>)?.Value;
        if (hero == null || slot.Crop == null)
        {
            slot.Status.Text = Loc.T("draft_teach_need_hero");
            return;
        }
        App.Host.Vision.Teach(hero.Key, slot.Crop);
        slot.Status.Text = Loc.T("draft_taught");
        UpdateMode();
    }

    private static FrameworkElement ReasonLine(string glyph, string brush, string text)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var icon = new Icon { Glyph = Ui.Glyph(glyph), Size = 14, Brush = Ui.Res(brush), Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(new TextBlock { Text = text, Foreground = Ui.Res(brush == "Red" ? "Red" : "Muted"), FontSize = 12.5, LineHeight = 19, TextWrapping = TextWrapping.Wrap });
        return dock;
    }

    /// <summary>Сводка по драфту противника: соперники по линии, угрозы, первый ситуативный предмет.</summary>
    private void ShowBriefing()
    {
        var rows = App.Host.BriefingRows(8);
        BriefCard.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BriefPanel.Children.Clear();
        int i = 1;
        foreach (var (title, text) in rows) BriefPanel.Children.Add(Ui.Numbered(i++, text, title));
    }

    private void ShowAnalysis(DraftAnalysis? analysis)
    {
        ShowBriefing();
        var L = Loc.Instance;
        NotesPanel.Children.Clear();
        ResultsPanel.Children.Clear();
        AltPanel.Children.Clear();
        AltExpander.Visibility = Visibility.Visible;
        if (analysis == null)
        {
            ResultsPanel.Children.Add(Ui.State(UiState.Empty, L.Get("state_draft_empty_title"), L.Get("draft_no_analysis")));
            AltExpander.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var n in analysis.Notes)
            NotesPanel.Children.Add(n.Kind == ReasonKind.Risk
                ? ReasonLine("I.Alert", "Warning", n.Text(L.Language))
                : ReasonLine("I.Info", "Muted", n.Text(L.Language)));
        var enemiesNow = App.Host.AutoDraftLive ? App.Host.AutoEnemies : App.Host.Draft.Enemies;
        foreach (var enemy in enemiesNow)
            if (App.Host.NoteFor(enemy) is string note)
                NotesPanel.Children.Add(ReasonLine("I.Spark", "Accent", L.Format("notes_line", enemy.Name, note)));

        int index = 1;
        foreach (var rec in analysis.Top)
        {
            var card = new Border { Style = (Style)FindResource("InnerCard"), Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(18, 16, 18, 16) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new TextBlock
            {
                Text = index.ToString("00"),
                FontFamily = (FontFamily)FindResource("DisplayFont"),
                FontSize = 28,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ui.Res(index == 1 ? "Accent" : "Faint")
            });
            var sp = new StackPanel();
            var headRow = Ui.Row(Ui.Up(rec.Hero.DisplayName(L.Language)), "", L.Format("conf_label", L.ConfidenceName(rec.Confidence)),
                rec.Confidence == Confidence.High ? "Accent" : rec.Confidence == Confidence.Medium ? "Purple" : "Muted");
            headRow.Margin = new Thickness(0, 4, 0, 8);
            var pic = Ui.Portrait(rec.Hero, 72);
            pic.Margin = new Thickness(0, 0, 14, 0);
            sp.Children.Add(Ui.WithImage(pic, headRow));
            foreach (var r in rec.Positives.Take(5)) sp.Children.Add(ReasonLine("I.Check", "Accent", r.Text(L.Language)));
            foreach (var r in rec.Risks.Take(3)) sp.Children.Add(ReasonLine("I.Alert", "Red", r.Text(L.Language)));
            Grid.SetColumn(sp, 1);
            grid.Children.Add(sp);
            card.Child = grid;
            ResultsPanel.Children.Add(card);
            // карточки контрпиков появляются по очереди
            Motion.Enter(card, 0, 14, (index - 1) * 70);
            index++;
        }
        if (analysis.Top.Count == 0)
            ResultsPanel.Children.Add(Ui.State(UiState.Empty, L.Get("state_draft_none_title"), L.Get("state_draft_none_text")));

        foreach (var rec in analysis.Alternatives)
        {
            var detail = string.Join("; ", rec.Positives.Where(r => r.Kind != ReasonKind.RoleFit).Take(2).Select(r => r.Text(L.Language)));
            AltPanel.Children.Add(Ui.Numbered(index++, rec.Hero.DisplayName(L.Language), detail, L.ConfidenceName(rec.Confidence)));
        }
        AltExpander.Visibility = analysis.Alternatives.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
