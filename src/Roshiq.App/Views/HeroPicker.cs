using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Core.Text;

namespace Roshiq.App.Views;

/// <summary>
/// Выбор героя сеткой портретов, как в экране пиков Dota: поиск (в любой раскладке), группы по атрибуту.
/// Уже выбранные в других слотах герои приглушены. Esc — закрыть, Enter — первый найденный.
/// </summary>
public sealed class HeroPicker : Window
{
    private readonly TextBox _search = new() { FontSize = 15, Padding = new Thickness(10, 8, 10, 8) };
    private readonly StackPanel _groups = new();
    private readonly IReadOnlySet<int> _taken;
    private readonly Action<Hero?> _picked;
    private readonly List<Hero> _all;
    private bool _closing;

    public HeroPicker(string title, IReadOnlySet<int> taken, bool canClear, Action<Hero?> picked)
    {
        _taken = taken;
        _picked = picked;
        _all = App.Host.Database.Heroes.ToList();
        Title = "ROSHIQ";
        Width = 860;
        Height = 640;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI");
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (canClear)
        {
            var clear = new Button { Content = Loc.T("picker_clear"), Style = (Style)Application.Current.FindResource("GhostButton") };
            clear.Click += (_, _) => Choose(null);
            DockPanel.SetDock(clear, Dock.Right);
            header.Children.Add(clear);
        }
        header.Children.Add(new TextBlock
        {
            Text = title, Style = (Style)Application.Current.FindResource("Eyebrow"), Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        });

        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(_search);
        var hint = new TextBlock { Text = Loc.T("picker_hint"), Style = (Style)Application.Current.FindResource("MutedText"), Margin = new Thickness(2, 8, 0, 0) };
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(new ScrollViewer { Content = _groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) });

        Content = new Border
        {
            Background = Ui.Res("Panel"), BorderBrush = Ui.Res("LineStrong"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Margin = new Thickness(10), Child = root,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45 }
        };

        _search.TextChanged += (_, _) => Fill();
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; SafeClose(); }
            else if (e.Key == Key.Enter) { e.Handled = true; var first = Filtered().FirstOrDefault(h => !_taken.Contains(h.Id)); if (first != null) Choose(first); }
        };
        Loaded += (_, _) => { _search.Focus(); Keyboard.Focus(_search); };
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => SafeClose();
        Fill();
    }

    private IEnumerable<Hero> Filtered()
    {
        var q = _search.Text.Trim();
        if (q.Length == 0) return _all;
        return _all.Select(h => (h, score: QuickSearch.Best(q, new[] { h.Name, h.NameRu, h.Key.Replace("npc_dota_hero_", "") })))
            .Where(x => x.score > 0).OrderByDescending(x => x.score).Select(x => x.h);
    }

    private void Fill()
    {
        var L = Loc.Instance;
        _groups.Children.Clear();
        var list = Filtered().ToList();
        bool searching = _search.Text.Trim().Length > 0;
        if (!searching)
        {
            var favorites = list.Where(h => App.Host.Settings.Current.FavoriteHeroKeys.Contains(h.Key)).ToList();
            var recentIds = App.Host.History.All().OrderByDescending(m => m.StartedUtc).Select(m => m.HeroId).Distinct().Take(6).ToList();
            var recent = recentIds.Select(id => list.FirstOrDefault(h => h.Id == id)).OfType<Hero>().Where(h => favorites.All(f => f.Id != h.Id)).ToList();
            foreach (var section in new[] { ("picker_favorites", favorites), ("picker_recent", recent) })
            {
                if (section.Item2.Count == 0) continue;
                _groups.Children.Add(new TextBlock { Text = L.Get(section.Item1), Style = (Style)Application.Current.FindResource("Eyebrow"), Margin = new Thickness(2, 10, 0, 6) });
                var shortcuts = new WrapPanel();
                foreach (var h in section.Item2) shortcuts.Children.Add(Tile(h));
                _groups.Children.Add(shortcuts);
            }
            var shown = favorites.Concat(recent).Select(h => h.Id).ToHashSet();
            list = list.Where(h => !shown.Contains(h.Id)).ToList();
        }
        var groups = searching
            ? new[] { (Title: (string?)null, Heroes: list) }
            : Enum.GetValues<PrimaryAttribute>().Select(a => (Title: (string?)L.Get("attr_" + a.ToString().ToLowerInvariant()),
                Heroes: list.Where(h => h.Attribute == a).OrderBy(h => h.DisplayName(L.Language)).ToList())).ToArray();
        foreach (var (title, heroes) in groups)
        {
            if (heroes.Count == 0) continue;
            if (title != null)
                _groups.Children.Add(new TextBlock
                {
                    Text = title, Style = (Style)Application.Current.FindResource("Eyebrow"), Margin = new Thickness(2, 10, 0, 6)
                });
            var wrap = new WrapPanel();
            foreach (var h in heroes) wrap.Children.Add(Tile(h));
            _groups.Children.Add(wrap);
        }
        if (list.Count == 0)
            _groups.Children.Add(new TextBlock { Text = L.Get("srch_none"), Style = (Style)Application.Current.FindResource("MutedText") });
    }

    private FrameworkElement Tile(Hero h)
    {
        bool taken = _taken.Contains(h.Id);
        var portrait = Ui.Portrait(h, 96);
        portrait.ToolTip = null;
        var panel = new StackPanel { Width = 96, Margin = new Thickness(0, 0, 8, 10), Opacity = taken ? 0.3 : 1, Cursor = taken ? Cursors.No : Cursors.Hand };
        panel.Children.Add(portrait);
        panel.Children.Add(new TextBlock
        {
            Text = h.DisplayName(Loc.Instance.Language), FontSize = 11.5, Foreground = Ui.Res("Muted"),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(1, 4, 0, 0)
        });
        panel.ToolTip = taken ? Loc.T("picker_taken") : h.DisplayName(Loc.Instance.Language);
        if (!taken)
        {
            panel.MouseEnter += (_, _) => portrait.BorderBrush = Ui.Res("Accent");
            panel.MouseLeave += (_, _) => portrait.BorderBrush = Ui.AttributeBrush(h.Attribute);
            panel.MouseLeftButtonUp += (_, _) => Choose(h);
        }
        return panel;
    }

    private void Choose(Hero? h)
    {
        SafeClose();
        _picked(h);
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
