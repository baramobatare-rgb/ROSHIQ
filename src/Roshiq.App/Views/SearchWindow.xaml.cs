using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Roshiq.App.Services;
using Roshiq.Core.Localization;

namespace Roshiq.App.Views;

/// <summary>Быстрый поиск по программе (Ctrl+K): разделы, настройки, герои, предметы, таймеры.</summary>
public partial class SearchWindow : Window
{
    private readonly IReadOnlyList<SearchEntry> _index;
    private bool _closing;

    public SearchWindow()
    {
        InitializeComponent();
        _index = SearchIndex.Build(App.Host);
        ShowResults(SearchIndex.Find(_index, ""));
        Loaded += (_, _) => { Query.Focus(); Keyboard.Focus(Query); };
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => { if (!_closing) Close(); };
    }

    /// <summary>Открыть поиск над главным окном (повторный Ctrl+K — закрыть).</summary>
    public static void Toggle(Window owner)
    {
        var open = owner.OwnedWindows.OfType<SearchWindow>().FirstOrDefault();
        if (open != null) { if (!open._closing) open.Close(); return; }
        new SearchWindow { Owner = owner }.Show();
    }

    private void ShowResults(IReadOnlyList<SearchEntry> found)
    {
        Results.Items.Clear();
        foreach (var e in found) Results.Items.Add(new ListBoxItem { Content = Row(e), Tag = e, Padding = new Thickness(8, 6, 8, 6) });
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
        Hint.Text = found.Count == 0 ? Loc.T("srch_none") : Loc.T("srch_hint");
    }

    private static FrameworkElement Row(SearchEntry e)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (e.Icon?.Invoke() is ImageSource img)
            grid.Children.Add(new Border
            {
                Width = 36, Height = 22, CornerRadius = new CornerRadius(4), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Image { Source = img, Stretch = Stretch.UniformToFill }
            });
        else
            grid.Children.Add(new Roshiq.Overlay.Icon
            {
                Glyph = Ui.Glyph(e.KindKey switch
                {
                    "srch_kind_section" => "I.Grid", "srch_kind_setting" => "I.Sliders", "srch_kind_timer" => "I.Clock", _ => "I.Search"
                }),
                Size = 16, Brush = Ui.Res("Accent"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0, 0, 0)
            });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = e.Title, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Text"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(e.Subtitle))
            text.Children.Add(new TextBlock { Text = e.Subtitle, FontSize = 11.5, Foreground = Ui.Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var kind = new TextBlock { Text = Loc.T(e.KindKey).ToUpperInvariant(), FontSize = 10, Foreground = Ui.Res("Faint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(kind, 2);
        grid.Children.Add(kind);
        return grid;
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e) => ShowResults(SearchIndex.Find(_index, Query.Text));

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (Results.Items.Count > 0) Results.SelectedIndex = (Results.SelectedIndex + 1) % Results.Items.Count;
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (Results.Items.Count > 0) Results.SelectedIndex = (Results.SelectedIndex - 1 + Results.Items.Count) % Results.Items.Count;
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                if (!_closing) Close();
                e.Handled = true;
                break;
        }
    }

    private void Results_MouseUp(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if ((Results.SelectedItem as ListBoxItem)?.Tag is not SearchEntry entry) return;
        if (!_closing) Close();
        try { entry.Open(); }
        catch (Exception ex) { Roshiq.Core.Diagnostics.Log.Error("Поиск: открыть " + entry.Title, ex); }
    }
}
