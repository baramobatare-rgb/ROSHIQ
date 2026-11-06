using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;

namespace Roshiq.App.Views;

public partial class PersonalBuildEditor : Window
{
    private readonly PersonalBuild _draft;
    private readonly string _original;
    private bool _loading = true;
    private bool _saved;
    private Point _dragStart;
    private PersonalBuildEntry? _dragEntry;
    private PersonalBuildEntry? Selected => (EntryList.SelectedItem as Choice<PersonalBuildEntry>)?.Value;
    public PersonalBuild Result => _draft;

    public PersonalBuildEditor(PersonalBuild build)
    {
        _draft = build.Copy();
        _original = JsonSerializer.Serialize(_draft);
        InitializeComponent();
        Ui.ItemTemplate(ItemBox);
        Ui.ItemTemplate(AlternativeBox);
        Ui.ItemTemplate(EntryList);
        if (Application.Current.MainWindow is Window owner && !ReferenceEquals(owner, this) && owner.IsLoaded) Owner = owner;
        Title = "ROSHIQ · " + Loc.T("my_editor");
        var hero = App.Host.Database.FindHero(build.HeroKey);
        ContextLabel.Text = (hero?.DisplayName(Loc.Instance.Language) ?? build.HeroKey) + " / " + Loc.Instance.PositionName(build.Position);
        NameBox.Text = build.Name;
        NotesBox.Text = build.Notes;
        foreach (var phase in Enum.GetValues<BuildPhase>())
        {
            AddPhase.Items.Add(new Choice<BuildPhase>(phase, Loc.Instance.PhaseName(phase)));
            EntryPhase.Items.Add(new Choice<BuildPhase>(phase, Loc.Instance.PhaseName(phase)));
        }
        AddPhase.SelectedIndex = 0;
        FillSearch();
        RenderRows();
        _loading = false;
        Closing += CheckUnsaved;
    }

    private string ItemLabel(Item item) => item.DisplayName(Loc.Instance.Language);
    private void FillSearch()
    {
        var query = ItemSearch.Text.Trim();
        ItemBox.ItemsSource = App.Host.Database.Items.Where(i => query.Length == 0 ||
            i.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || i.NameRu.Contains(query, StringComparison.OrdinalIgnoreCase)
            || i.Key.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.DisplayName(Loc.Instance.Language))
            .Select(i => Ui.ItemChoice(i, i.Key, ItemLabel(i))).ToList();
        ItemBox.SelectedIndex = ItemBox.Items.Count > 0 ? 0 : -1;
        AddButton.IsEnabled = ItemBox.Items.Count > 0;
        SearchStatus.Text = Loc.T("my_no_items");
        SearchStatus.Visibility = ItemBox.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (!_loading) FillSearch(); }
    private void RenderRows(PersonalBuildEntry? keep = null)
    {
        bool previous = _loading;
        _loading = true;
        var rows = _draft.Entries.OrderBy(e => e.Phase).Select(e => Ui.ItemChoice(e, e.ItemKey,
            Loc.Instance.PhaseName(e.Phase) + " / " + (App.Host.Database.FindItem(e.ItemKey) is Item item ? ItemLabel(item) : e.ItemKey + " · " + Loc.T("my_missing_item")))).ToList();
        EntryList.ItemsSource = rows;
        EntryList.SelectedItem = rows.FirstOrDefault(x => ReferenceEquals(x.Value, keep)) ?? rows.FirstOrDefault();
        EmptyLabel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _loading = previous;
        FillEntryFields();
    }
    private void FillEntryFields()
    {
        bool previous = _loading;
        _loading = true;
        var entry = Selected;
        EntryFields.IsEnabled = RemoveButton.IsEnabled = entry != null;
        var siblings = _draft.Entries.Where(x => x.Phase == entry?.Phase).ToList();
        UpButton.IsEnabled = entry != null && siblings.IndexOf(entry) > 0;
        DownButton.IsEnabled = entry != null && siblings.IndexOf(entry) < siblings.Count - 1;
        SelectedItemTitle.Text = entry == null ? "—" : App.Host.Database.FindItem(entry.ItemKey)?.DisplayName(Loc.Instance.Language) ?? entry.ItemKey;
        EntryNote.Text = entry?.Note ?? "";
        if (entry != null) Ui.SelectValue(EntryPhase, entry.Phase); else EntryPhase.SelectedIndex = -1;
        AlternativeBox.Items.Clear();
        AlternativeBox.Items.Add(new Choice<string?>(null, Loc.T("my_none")));
        foreach (var item in App.Host.Database.Items.Where(x => x.Key != entry?.ItemKey).OrderBy(x => x.DisplayName(Loc.Instance.Language)))
            AlternativeBox.Items.Add(Ui.ItemChoice<string?>(item.Key, item.Key, ItemLabel(item)));
        Ui.SelectValue(AlternativeBox, entry?.AlternativeKey);
        if (AlternativeBox.SelectedIndex < 0) AlternativeBox.SelectedIndex = 0;
        _loading = previous;
    }
    private void Entry_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) FillEntryFields(); }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.TryValue<Item>(ItemBox, out var item) || !Ui.TryValue<BuildPhase>(AddPhase, out var phase)) return;
        var entry = new PersonalBuildEntry { ItemKey = item.Key, Phase = phase };
        _draft.Entries.Add(entry);
        RenderRows(entry);
        EntryList.ScrollIntoView(EntryList.SelectedItem);
    }
    private void Note_Changed(object sender, TextChangedEventArgs e) { if (!_loading && Selected is { } entry) entry.Note = EntryNote.Text; }
    private void Alternative_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && Selected is { } entry && Ui.TryValue<string?>(AlternativeBox, out var key)) entry.AlternativeKey = key;
    }
    private void Phase_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Selected is not { } entry || !Ui.TryValue<BuildPhase>(EntryPhase, out var phase)) return;
        entry.Phase = phase;
        RenderRows(entry);
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        _draft.Entries.Remove(entry); RenderRows();
    }
    private void Move(int direction)
    {
        if (Selected is not { } entry) return;
        var siblings = _draft.Entries.Where(x => x.Phase == entry.Phase).ToList();
        int target = siblings.IndexOf(entry) + direction;
        if (target < 0 || target >= siblings.Count) return;
        int a = _draft.Entries.IndexOf(entry), b = _draft.Entries.IndexOf(siblings[target]);
        (_draft.Entries[a], _draft.Entries[b]) = (_draft.Entries[b], _draft.Entries[a]);
        RenderRows(entry);
    }
    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Entry_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(EntryList);
        _dragEntry = (ItemsControl.ContainerFromElement(EntryList, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext is Choice<PersonalBuildEntry> row ? row.Value : null;
    }
    private void Entry_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragEntry == null) return;
        var delta = e.GetPosition(EntryList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var entry = _dragEntry;
        _dragEntry = null;
        DragDrop.DoDragDrop(EntryList, new DataObject(typeof(PersonalBuildEntry), entry), DragDropEffects.Move);
    }
    private void Entry_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PersonalBuildEntry)) is not PersonalBuildEntry entry || !_draft.Entries.Contains(entry)) return;
        var target = (ItemsControl.ContainerFromElement(EntryList, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext as Choice<PersonalBuildEntry>;
        if (target == null || ReferenceEquals(target.Value, entry)) return;
        _draft.Entries.Remove(entry);
        entry.Phase = target.Value.Phase;
        _draft.Entries.Insert(_draft.Entries.IndexOf(target.Value), entry);
        RenderRows(entry);
        e.Handled = true;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _draft.Name = NameBox.Text.Trim(); _draft.Notes = NotesBox.Text;
        if (_draft.Name.Length == 0 || !_draft.Entries.Any(x => x.Phase is BuildPhase.Start or BuildPhase.Early or BuildPhase.Mid or BuildPhase.Late
            && App.Host.Database.FindItem(x.ItemKey) != null))
        { ErrorText.Text = Loc.T("my_save_error"); return; }
        _draft.Normalize(); _saved = true; DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void CheckUnsaved(object? sender, CancelEventArgs e)
    {
        if (_saved) return;
        _draft.Name = NameBox.Text; _draft.Notes = NotesBox.Text;
        if (JsonSerializer.Serialize(_draft) != _original && MessageBox.Show(this, Loc.T("my_unsaved"), Loc.T("my_editor"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
    }
}
