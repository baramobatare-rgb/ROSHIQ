using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Draft;
using Roshiq.Recommendations;

namespace Roshiq.App.Views;

/// <summary>
/// 0.23.0: тир-лист — сильные сейчас герои для каждой позиции (доля побед в выбранных рангах, OpenDota /heroStats)
/// и контрпики к выбранному герою (тот же анализ, что в драфте: правила + статистика матчапов).
/// </summary>
public partial class TierListView : UserControl, IRefreshable
{
    private Position _position = Position.Carry;
    private RankBracket _bracket;
    private Hero? _selected;
    private bool _loading;
    private bool _ready;

    private static readonly (RankBracket B, string Key)[] Ranks =
    {
        (RankBracket.All, "tier_rank_all"), (RankBracket.Low, "tier_rank_low"), (RankBracket.Mid, "tier_rank_mid"),
        (RankBracket.High, "tier_rank_high"), (RankBracket.Immortal, "tier_rank_immortal"), (RankBracket.Pro, "tier_rank_pro")
    };

    public TierListView()
    {
        InitializeComponent();
        _position = App.Host.CurrentPosition;
        _bracket = TierList.FromRankTier(App.Host.Settings.Current.Data.RankTier);
        BuildControls();
        Loc.Instance.PropertyChanged += (_, _) => Dispatcher.InvokeAsync(() => { BuildControls(); Refresh(); });
        _ready = true;
    }

    private void BuildControls()
    {
        var L = Loc.Instance;
        PositionTabs.Children.Clear();
        foreach (var p in Enum.GetValues<Position>())
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "TierPos", Content = L.PositionName(p),
                IsChecked = p == _position, Margin = new Thickness(0, 0, 8, 8), Tag = p
            };
            rb.Checked += (_, _) => { _position = (Position)rb.Tag; _selected = null; Render(); };
            PositionTabs.Children.Add(rb);
        }
        _ready = false;
        RankBox.Items.Clear();
        foreach (var (b, key) in Ranks) RankBox.Items.Add(new ComboBoxItem { Content = L.Get(key), Tag = b });
        RankBox.SelectedIndex = Array.FindIndex(Ranks, r => r.B == _bracket);
        _ready = true;
    }

    public void Refresh()
    {
        Render();
        if (!App.Host.TierStats.IsFresh(DateTime.UtcNow)) _ = LoadAsync(force: false);
    }

    private async Task LoadAsync(bool force)
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        if (App.Host.TierStats.Get() == null) StatusText.Text = Loc.T("tier_loading");
        try
        {
            var (_, message) = await App.Host.LoadTierStatsAsync(force);
            Render();
            if (message != null) StatusText.Text = message;
        }
        finally { _loading = false; RefreshButton.IsEnabled = true; }
    }

    private void Render()
    {
        var L = Loc.Instance;
        TiersPanel.Children.Clear();
        var stats = App.Host.TierStats.Get();
        if (stats == null)
        {
            CounterCard.Visibility = Visibility.Collapsed;
            if (!_loading) StatusText.Text = App.Host.Settings.Current.Data.AllowOnlineStats ? L.Get("tier_none") : L.Get("steam_need_network");
            return;
        }
        var entries = TierList.Build(stats, App.Host.Database.Heroes, _position, _bracket);
        StatusText.Text = L.Format("tier_status", entries.Count, stats.FetchedUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture));
        if (entries.Count == 0) { TiersPanel.Children.Add(Ui.Text(L.Get("tier_empty"), "MutedText")); CounterCard.Visibility = Visibility.Collapsed; return; }

        _selected ??= entries[0].Hero;
        int index = 0;
        foreach (var tier in TierList.Tiers)
        {
            var group = entries.Where(e => e.Tier == tier).ToList();
            if (group.Count == 0) continue;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var badge = new Border
            {
                Width = 52, MinHeight = 52, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Stretch,
                Background = TierBrush(tier),
                Child = new TextBlock { Text = tier, FontSize = 24, FontWeight = FontWeights.Black, Foreground = Ui.Res("Bg"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);
            var wrap = new WrapPanel();
            foreach (var e in group) wrap.Children.Add(Tile(e, index++, L));
            row.Children.Add(wrap);
            var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 10), Child = row };
            TiersPanel.Children.Add(card);
        }
        ShowCounters(entries.FirstOrDefault(e => e.Hero.Id == _selected.Id) ?? entries[0], L);
    }

    private static Brush TierBrush(string tier) => tier switch
    {
        "S" => Ui.Res("Accent"),
        "A" => new SolidColorBrush(Color.FromRgb(0x8F, 0xD6, 0x5E)),
        "B" => new SolidColorBrush(Color.FromRgb(0xE8, 0xC5, 0x4A)),
        "C" => new SolidColorBrush(Color.FromRgb(0xE8, 0x93, 0x4A)),
        _ => Ui.Res("Red")
    };

    private FrameworkElement Tile(TierEntry e, int index, Loc L)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(Ui.Portrait(e.Hero, 64));
        var text = new StackPanel { Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = e.Hero.Name, Foreground = Ui.Res("Text"), FontWeight = FontWeights.SemiBold, FontSize = 13 });
        text.Children.Add(new TextBlock
        {
            Text = L.Format("tier_tile", Pct(e.WinRate), Pct(e.PickRate)), Foreground = Ui.Res("Muted"), FontSize = 11.5
        });
        stack.Children.Add(text);
        var button = new Button
        {
            Style = (Style)FindResource("GhostButton"), Content = stack, Padding = new Thickness(6, 5, 10, 5), Margin = new Thickness(0, 0, 8, 8),
            ToolTip = L.Format("tier_tip", e.Games.ToString("N0", CultureInfo.CurrentCulture)),
            BorderBrush = _selected?.Id == e.Hero.Id ? Ui.Res("Accent") : null
        };
        button.Click += (_, _) => { _selected = e.Hero; Render(); CounterCard.BringIntoView(); };
        Motion.Enter(button, 0, 8, Math.Min(index, 24) * 18);
        return button;
    }

    private void ShowCounters(TierEntry e, Loc L)
    {
        CounterPanel.Children.Clear();
        CounterCard.Visibility = Visibility.Visible;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var portrait = Ui.Portrait(e.Hero, 84);
        DockPanel.SetDock(portrait, Dock.Left);
        head.Children.Add(portrait);
        var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = L.Format("tier_counter_title", e.Hero.Name), Style = (Style)FindResource("H2"), Margin = new Thickness(0) });
        info.Children.Add(Ui.Text(L.Format("tier_counter_sub", e.Tier, Pct(e.WinRate), Pct(e.PickRate), L.PositionName(_position)), "MutedText"));
        head.Children.Add(info);
        CounterPanel.Children.Add(head);

        var analysis = App.Host.Analyzer.Analyze(new DraftState(Array.Empty<Hero>(), new[] { e.Hero }, _position), top: 3, alternatives: 0, countersOnly: true);
        if (analysis.Top.Count == 0) { CounterPanel.Children.Add(Ui.Text(L.Get("tier_no_counters"), "MutedText")); return; }
        var wrap = new WrapPanel();
        foreach (var rec in analysis.Top)
        {
            var box = new Border
            {
                Background = Ui.Res("Panel2"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 10), Width = 300
            };
            var row = new DockPanel();
            var p = Ui.Portrait(rec.Hero, 56);
            DockPanel.SetDock(p, Dock.Left);
            row.Children.Add(p);
            var t = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
            t.Children.Add(new TextBlock { Text = rec.Hero.Name, Foreground = Ui.Res("Text"), FontWeight = FontWeights.SemiBold });
            var why = rec.Positives.FirstOrDefault();
            if (why != null) t.Children.Add(new TextBlock { Text = L.Language == Roshiq.Core.Models.Language.English ? why.En : why.Ru, Foreground = Ui.Res("Muted"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            if (rec.Stats.FirstOrDefault() is { } st && st.Games > 0)
                t.Children.Add(new TextBlock { Text = L.Format("tier_counter_stat", Pct((double)st.Wins / st.Games), st.Games.ToString("N0", CultureInfo.CurrentCulture)), Foreground = Ui.Res("Faint"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) });
            row.Children.Add(t);
            box.Child = row;
            wrap.Children.Add(box);
        }
        CounterPanel.Children.Add(wrap);
        Motion.Enter(CounterCard, 0, 8);
    }

    private static string Pct(double v) => (v * 100).ToString("0.0", CultureInfo.CurrentCulture) + "%";

    private void RankBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || RankBox.SelectedItem is not ComboBoxItem { Tag: RankBracket b }) return;
        _bracket = b;
        Render();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(force: true);
}
