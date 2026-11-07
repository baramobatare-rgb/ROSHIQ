using System.Windows;
using System.Windows.Controls;
using Roshiq.App.Services;
using Roshiq.Core.Localization;

namespace Roshiq.App.Views;

/// <summary>«Что нового» — коротко о главном после обновления (из встроенного Assets/whatsnew.txt).</summary>
public partial class WhatsNewWindow : Window
{
    public sealed record Entry(Version Version, string Ru, string En);

    public WhatsNewWindow(IReadOnlyList<Entry> entries)
    {
        InitializeComponent();
        var L = Loc.Instance;
        VersionLabel.Text = "ROSHIQ " + AppUpdater.CurrentVersion;
        foreach (var group in entries.GroupBy(e => e.Version).OrderByDescending(g => g.Key))
        {
            ItemsPanel.Children.Add(new TextBlock
            {
                Text = "v" + group.Key, Style = (Style)FindResource("Eyebrow"), Margin = new Thickness(0, 8, 0, 4)
            });
            foreach (var e in group)
            {
                var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var icon = new Roshiq.Overlay.Icon { Glyph = Ui.Glyph("I.Spark"), Size = 14, Brush = Ui.Res("Accent"), Margin = new Thickness(0, 2, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(icon, Dock.Left);
                dock.Children.Add(icon);
                dock.Children.Add(new TextBlock
                {
                    Text = L.Language == Roshiq.Core.Models.Language.English ? e.En : e.Ru, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Text")
                });
                ItemsPanel.Children.Add(dock);
            }
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Все записи из встроенного файла.</summary>
    public static IReadOnlyList<Entry> Load()
    {
        var list = new List<Entry>();
        try
        {
            using var stream = typeof(WhatsNewWindow).Assembly.GetManifestResourceStream("Roshiq.App.Assets.whatsnew.txt");
            if (stream == null) return list;
            using var reader = new StreamReader(stream);
            foreach (var raw in reader.ReadToEnd().Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var f = line.Split('|');
                if (f.Length >= 3 && Version.TryParse(f[0].Trim(), out var v)) list.Add(new Entry(v, f[1].Trim(), f[2].Trim()));
            }
        }
        catch (Exception ex) { Roshiq.Core.Diagnostics.Log.Warn("whatsnew.txt: " + ex.Message); }
        return list;
    }

    /// <summary>Что показать при обновлении с lastSeen до current (не больше двух последних версий).</summary>
    public static IReadOnlyList<Entry> Since(IReadOnlyList<Entry> all, string? lastSeen, Version current)
    {
        Version.TryParse(lastSeen, out var seen);
        var versions = all.Select(e => e.Version).Distinct().Where(v => v <= current && (seen == null || v > seen))
            .OrderByDescending(v => v).Take(2).ToHashSet();
        return all.Where(e => versions.Contains(e.Version)).ToList();
    }
}
