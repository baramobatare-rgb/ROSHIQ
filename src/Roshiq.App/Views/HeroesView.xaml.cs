using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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

    /// <summary>Герой, которого выбрать при следующем обновлении (переход из поиска Ctrl+K).</summary>
    public static string? PendingHeroKey { get; set; }

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
        var keep = PendingHeroKey != null ? App.Host.Database.FindHero(PendingHeroKey) ?? _selected : _selected;
        PendingHeroKey = null;
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
        ShowMeta(hero, autoLoad: true);

        FavoriteButton.Content = L.Get(App.Host.Settings.Current.FavoriteHeroKeys.Contains(hero.Key) ? "my_favorite_remove" : "my_favorite_add");
        FavoriteButton.Foreground = Ui.Res(App.Host.Settings.Current.FavoriteHeroKeys.Contains(hero.Key) ? "Accent" : "Text");
        HeroName.Text = hero.DisplayName(L.Language);
        if ((PortraitHost.Content as FrameworkElement)?.Tag as string != hero.Key)
        {
            SetBackdrop(hero);
            var portrait = Ui.Portrait(hero, 136);
            portrait.Tag = hero.Key;
            PortraitHost.Content = portrait;
            Motion.Enter(portrait, -10, 0);
        }
        HeroMeta.Text = Ui.Up($"{hero.Name} · {L.Get("attr_" + hero.Attribute.ToString().ToLowerInvariant())} · " +
                        string.Join(" / ", hero.Positions.Select(p => "POS " + (int)p)));
        TraitPanel.Children.Clear();
        // признаки — одной строкой: первые четыре, остальные в подсказке «+N»
        var traits = hero.Traits.OrderBy(t => t).Select(t => db.TraitText(t, L.Language)).ToList();
        foreach (var t in traits.Take(4)) TraitPanel.Children.Add(Ui.Chip(t));
        if (traits.Count > 4)
        {
            var more = Ui.Chip("+" + (traits.Count - 4));
            more.ToolTip = string.Join("\n", traits.Skip(4));
            TraitPanel.Children.Add(more);
        }
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

        ShowAdaptations(hero, position, build);

        // Текущая стадия матча — только если это герой, которым игрок играет сейчас (по GSI)
        var gsi = App.Host.LastGsi;
        BuildPhase? now = null;
        if (gsi != null && gsi.IsInProgress && App.Host.CurrentHero?.Id == hero.Id && DateTime.UtcNow - gsi.ReceivedUtc < TimeSpan.FromSeconds(40))
            now = AppHost.PhaseForClock(gsi.ClockTime);

        BuildPanel.Children.Clear();
        var timings = MyTimings(hero);
        int n = 1;
        foreach (var phase in new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late, BuildPhase.Situational, BuildPhase.Alternative })
        {
            var picks = build[phase];
            if (picks.Count == 0) continue;

            var head = new DockPanel { Margin = new Thickness(0, n == 1 ? 8 : 12, 0, 8) };
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

            var grid = new WrapPanel();
            foreach (var p in picks)
                grid.Children.Add(ItemTile(p.Item.Key, p.Item.DisplayName(L.Language), p.Explain(L.Language), p.Curated, now == phase,
                    timings.TryGetValue(p.Item.Key, out var tm) ? tm : App.Host.ItemNorms.Get(hero.Id, p.Item.Key, out _), timings.ContainsKey(p.Item.Key)));
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

    private List<Hero>? _previewEnemies;
    private IReadOnlyList<Hero> PreviewEnemies => _previewEnemies ?? App.Host.BriefingEnemies;

    private void AddEnemy_Click(object sender, RoutedEventArgs e)
    {
        var current = PreviewEnemies.Where(h => h.Id != _selected?.Id).DistinctBy(h => h.Id).Take(5).ToList();
        if (current.Count >= 5) return;
        var taken = current.Select(h => h.Id).ToHashSet();
        if (_selected != null) taken.Add(_selected.Id);
        new HeroPicker(Loc.T("build_add_enemy"), taken, false, hero =>
        {
            if (hero == null) return;
            _previewEnemies = current.Append(hero).DistinctBy(h => h.Id).Take(5).ToList();
            ShowDetails();
        }) { Owner = Window.GetWindow(this) }.Show();
    }

    private void UseDraftEnemies_Click(object sender, RoutedEventArgs e) { _previewEnemies = null; ShowDetails(); }

    private void ShowAdaptations(Hero hero, Position position, BuildRecommendation baseline)
    {
        var L = Loc.Instance;
        var enemies = PreviewEnemies.Where(e => e.Id != hero.Id).DistinctBy(e => e.Id).Take(5).ToList();
        EnemyContextPanel.Children.Clear();
        foreach (var enemy in enemies)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Ui.Portrait(enemy, 40));
            row.Children.Add(new TextBlock { Text = enemy.DisplayName(L.Language) + "  ×", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            var button = new Button { Content = row, ToolTip = L.Get("picker_clear"), Padding = new Thickness(7, 5, 7, 5) };
            button.Click += (_, _) => { _previewEnemies = enemies.Where(e => e.Id != enemy.Id).ToList(); ShowDetails(); };
            EnemyContextPanel.Children.Add(button);
        }
        AddEnemyButton.IsEnabled = enemies.Count < 5;
        AddEnemyButton.ToolTip = enemies.Count < 5 ? L.Get("build_add_enemy") : L.Get("build_enemy_limit");
        AdaptationPanel.Children.Clear();
        // пока врагов нет — блок сжат до одной строки (заголовок и кнопки), чтобы не вытеснять сборку
        bool any = enemies.Count > 0;
        EnemyContextPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        AdaptHint.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        AdaptationPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) return;
        var options = App.Host.Builds.Adaptations(hero, position, enemies, baseline);
        foreach (var option in options)
        {
            var text = new StackPanel();
            var title = Ui.Text(option.Pick.Item.DisplayName(L.Language), "Value"); title.FontWeight = FontWeights.SemiBold;
            text.Children.Add(title);
            text.Children.Add(Ui.Text(string.Join("; ", option.Pick.Reasons.Take(2).Select(reason => reason.Text(L.Language))), "Value"));
            text.Children.Add(Ui.Text(option.Pick.Item.Purpose(L.Language), "MutedText", 4));
            var action = option.AlreadyIncluded ? L.Get("build_included") : option.Replace != null
                ? L.Format("build_replace", option.Replace.DisplayName(L.Language)) : L.Get("build_consider");
            var label = Ui.Text(action, "MutedText", 6); label.Foreground = Ui.Res(option.AlreadyIncluded ? "Success" : "Accent");
            text.Children.Add(label);
            // информационный блок без рамки
            var block = Ui.WithImage(Ui.ItemIcon(option.Pick.Item.Key, 52), text);
            block.Margin = new Thickness(0, 0, 0, 12);
            AdaptationPanel.Children.Add(block);
        }
        if (options.Count == 0) AdaptationPanel.Children.Add(Ui.Text(L.Get("build_adapt_none"), "MutedText"));
    }

    /// <summary>Арт героя фоном шапки: справа ярко, к левому краю растворяется в цвет карточки.</summary>
    /// <summary>Арт героя справа в шапке: в родном размере (по высоте шапки), затемнён и растворяется влево — без растягивания.</summary>
    private void SetBackdrop(Hero hero)
    {
        HeaderArt.Source = Roshiq.App.Services.HeroArt.Portrait(hero.Key);
    }

    /// <summary>Ваше среднее время покупки предметов на этом герое (по истории, от двух матчей).</summary>
    private static Dictionary<string, int> MyTimings(Hero hero)
    {
        var games = App.Host.History.All().Where(m => m.HeroId == hero.Id && m.ItemTimings.Count > 0).Take(30).ToList();
        if (games.Count < 2) return new();
        return games.SelectMany(m => m.ItemTimings).GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 2).ToDictionary(g => g.Key, g => (int)g.Average(kv => kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Плитка предмета — одинаковая для всех: иконка в пропорциях Valve, название, одна строка назначения,
    /// цена и тайминг всегда в нижней строке на одних и тех же местах. Полное «почему» — в подсказке.
    /// </summary>
    private FrameworkElement ItemTile(string key, string name, string why, bool curated, bool now, int? timing, bool mine)
    {
        var L = Loc.Instance;
        var icon = Ui.ItemIcon(key, 66);
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var purpose = App.Host.Database.FindItem(key)?.Purpose(L.Language);
        if (string.IsNullOrWhiteSpace(purpose)) purpose = why;

        var text = new Grid { VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 3; i++) text.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Ui.Res("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        var line = new TextBlock
        {
            Text = purpose ?? "", FontSize = 12.5, Foreground = Ui.Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0)
        };
        Grid.SetRow(line, 1);
        text.Children.Add(line);

        // нижняя строка: цена — фиксированная ширина слева (даже если цены нет), тайминг — справа от неё
        var meta = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (App.Host.Costs.Cost(key) is int cost)
        {
            var gold = new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x4B));
            var price = new StackPanel { Orientation = Orientation.Horizontal };
            price.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = gold, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            price.Children.Add(new TextBlock { Text = cost.ToString("#,0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")), FontSize = 12.5, Foreground = gold });
            meta.Children.Add(price);
        }
        if (timing is int t && t > 0)
        {
            var tm = new TextBlock
            {
                Text = (mine ? L.Get("item_tile_mine") : L.Get("item_tile_usual")) + $" ~{t / 60}:{t % 60:00}",
                FontSize = 12.5, Foreground = Ui.Res(mine ? "Text" : "Faint"), TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(tm, 1);
            meta.Children.Add(tm);
        }
        Grid.SetRow(meta, 2);
        text.Children.Add(meta);

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);
        var card = new Border
        {
            Style = (Style)FindResource("InnerCard"), Width = 268, Height = 76, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(12, 8, 12, 8),
            BorderBrush = Ui.Res(now ? "Accent" : curated ? "LineStrong" : "Line"), Child = row,
            ToolTip = new ToolTip { Content = new TextBlock { Text = why + (curated ? "\n\n" + L.Get("build_base") : ""), TextWrapping = TextWrapping.Wrap, MaxWidth = 320 } }
        };
        ToolTipService.SetInitialShowDelay(card, 150);
        return card;
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

    // ------------------------------------------------------------------ мета-сборка из OpenDota

    private int _metaLoadingHero = -1;
    /// <summary>Герои, для которых уже пробовали загрузить мету в этот запуск (без повторов при каждой перерисовке).</summary>
    private readonly HashSet<int> _metaTried = new();

    private void ShowMeta(Hero hero, bool autoLoad)
    {
        var L = Loc.Instance;
        var meta = App.Host.MetaBuilds.Get(hero.Id);
        MetaPanel.Children.Clear();
        MetaUseButton.IsEnabled = meta != null;
        bool online = App.Host.Settings.Current.Data.AllowOnlineStats;
        MetaRefreshButton.IsEnabled = online && _metaLoadingHero != hero.Id;
        if (meta == null)
        {
            MetaStatus.Text = online ? L.Get("meta_none") : L.Get("steam_need_network");
        }
        else
        {
            MetaStatus.Text = L.Format("meta_updated", meta.FetchedUtc.ToLocalTime().ToString("dd.MM HH:mm"));
            foreach (var (phase, items) in meta.Phases.OrderBy(p => p.Key))
            {
                MetaPanel.Children.Add(new TextBlock
                {
                    Text = L.PhaseName(phase).ToUpperInvariant(), Style = (Style)FindResource("Eyebrow"), Margin = new Thickness(0, 8, 0, 4)
                });
                var wrap = new WrapPanel();
                foreach (var it in items)
                {
                    var item = App.Host.Database.FindItem(it.ItemKey);
                    var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6) };
                    var icon = Ui.ItemIcon(it.ItemKey);
                    chip.Children.Add(icon);
                    chip.Children.Add(new TextBlock
                    {
                        Text = item?.DisplayName(L.Language) ?? it.ItemKey, Foreground = Ui.Res("Text"), VerticalAlignment = VerticalAlignment.Center
                    });
                    chip.Children.Add(new TextBlock
                    {
                        Text = " " + (it.Share * 100).ToString("0") + "%", Foreground = Ui.Res("Muted"), VerticalAlignment = VerticalAlignment.Center
                    });
                    chip.ToolTip = L.Format("meta_count", it.Count);
                    wrap.Children.Add(chip);
                }
                MetaPanel.Children.Add(wrap);
            }
        }
        if (autoLoad && online && App.Host.MetaBuilds.IsStale(hero.Id) && _metaLoadingHero != hero.Id && _metaTried.Add(hero.Id))
            _ = LoadMetaAsync(hero, force: false);
    }

    private async Task LoadMetaAsync(Hero hero, bool force)
    {
        _metaLoadingHero = hero.Id;
        MetaRefreshButton.IsEnabled = false;
        MetaStatus.Text = Loc.T("meta_loading");
        string? message;
        try { (_, message) = await App.Host.LoadMetaBuildAsync(hero, force); }
        finally { _metaLoadingHero = -1; }
        if (_selected?.Id != hero.Id) return;
        ShowMeta(hero, autoLoad: false);
        if (message != null) MetaStatus.Text = message;
    }

    private void MetaRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) _ = LoadMetaAsync(_selected, force: true);
    }

    private void MetaUse_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || App.Host.MetaBuilds.Get(_selected.Id) is not { } meta) return;
        var build = App.Host.PersonalFromMeta(meta, _selected, SelectedPosition);
        OpenEditor(build);
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
