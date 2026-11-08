using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Roshiq.Core.Localization;
using Roshiq.Core.Models;
using Roshiq.Overlay;

namespace Roshiq.App.Views;

/// <summary>Элемент списка с отображаемым текстом.</summary>
public sealed class Choice<T>
{
    public Choice(T value, string display) { Value = value; Display = display; }
    public T Value { get; }
    public string Display { get; }
    /// <summary>Группа в списке (для героев — основной атрибут).</summary>
    public AttributeGroup? Group { get; init; }
    /// <summary>Вторая строка карточки (дата · длительность). null — однострочный элемент.</summary>
    public string? Subtitle { get; init; }
    /// <summary>Короткая видимая пометка справа во второй строке (например, «неполная запись»).</summary>
    public string? Note { get; init; }
    /// <summary>Картинка слева (портрет героя). null — без картинки.</summary>
    public ImageSource? Icon { get; init; }
    /// <summary>Заголовок группы (СИЛА, ЛОВКОСТЬ…) — не выбирается, только разделяет список.</summary>
    public bool IsHeader { get; init; }
    /// <summary>Сколько героев в группе (для заголовка).</summary>
    public int GroupCount { get; init; }
    public override string ToString() => Display;
}

/// <summary>Заголовок группы героев: атрибут, его цвет и название.</summary>
public sealed class AttributeGroup : IEquatable<AttributeGroup>
{
    public AttributeGroup(PrimaryAttribute attribute) { Attribute = attribute; }
    public PrimaryAttribute Attribute { get; }
    public string Display => Ui.Up(Loc.T("attr_" + Attribute.ToString().ToLowerInvariant()));
    public Brush Brush => Ui.AttributeBrush(Attribute);
    public bool Equals(AttributeGroup? other) => other != null && other.Attribute == Attribute;
    public override bool Equals(object? obj) => Equals(obj as AttributeGroup);
    public override int GetHashCode() => (int)Attribute;
    public override string ToString() => Display;
}

public enum UiState { Empty, Loading, Error, Disconnected }

/// <summary>Строительные блоки дизайн-системы для элементов, создаваемых из кода.</summary>
internal static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private static readonly Dictionary<PrimaryAttribute, Brush> AttributeBrushes = new()
    {
        [PrimaryAttribute.Strength] = Frozen(0xEC, 0x5B, 0x56),
        [PrimaryAttribute.Agility] = Frozen(0x4C, 0xC4, 0x6A),
        [PrimaryAttribute.Intelligence] = Frozen(0x4F, 0xA3, 0xF7),
        [PrimaryAttribute.Universal] = Frozen(0xE3, 0xB3, 0x41),
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Цвет атрибута как в игре: сила — красный, ловкость — зелёный, интеллект — синий, универсальный — золотой.</summary>
    public static Brush AttributeBrush(PrimaryAttribute attribute) => AttributeBrushes[attribute];

    /// <summary>Порядок групп: сила, ловкость, интеллект, универсальные.</summary>
    public static int AttributeOrder(PrimaryAttribute attribute) => (int)attribute;

    /// <summary>Элемент списка героя: портрет, цветная полоска атрибута, имя.</summary>
    public static Choice<T> HeroChoice<T>(T value, Hero hero, string display) =>
        new(value, display) { Group = new AttributeGroup(hero.Attribute), Icon = Roshiq.App.Services.HeroArt.Portrait(hero.Key) };

    /// <summary>Портрет героя 16:9 со скруглением и полоской цвета атрибута снизу.</summary>
    public static Border Portrait(Hero hero, double width)
    {
        var img = Roshiq.App.Services.HeroArt.Portrait(hero.Key);
        return new Border
        {
            Width = width,
            Height = Math.Round(width * 9 / 16),
            CornerRadius = new CornerRadius(width >= 100 ? 10 : 5),
            BorderThickness = new Thickness(0, 0, 0, width >= 100 ? 3 : 2),
            BorderBrush = AttributeBrush(hero.Attribute),
            Background = img != null ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : Res("Panel3"),
            ToolTip = hero.Name,
            SnapsToDevicePixels = true
        };
    }

    /// <summary>Иконка предмета (88×64 у Valve) или пустой квадрат, пока иконка не загружена.</summary>
    public static Border ItemIcon(string itemKey, double width = 34)
    {
        var img = Roshiq.App.Services.HeroArt.ItemIcon(itemKey);
        return new Border
        {
            Width = width,
            Height = Math.Round(width * 64 / 88),
            CornerRadius = new CornerRadius(4),
            Background = img != null ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : Res("Panel3"),
            BorderBrush = Res("Line"),
            BorderThickness = new Thickness(img != null ? 0 : 1),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 12, 0)
        };
    }

    /// <summary>Элемент списка предмета с иконкой (шаблон ItemChoiceTemplate).</summary>
    public static Choice<T> ItemChoice<T>(T value, string? itemKey, string display) =>
        new(value, display) { Icon = itemKey == null ? null : Roshiq.App.Services.HeroArt.ItemIcon(itemKey) };

    /// <summary>Настроить список или выпадающий список предметов: иконка + название, поиск по тексту.</summary>
    public static void ItemTemplate(ItemsControl control)
    {
        control.ItemTemplate = (DataTemplate)Application.Current.FindResource("ItemChoiceTemplate");
        TextSearch.SetTextPath(control, "Display");
    }

    /// <summary>Поставить иконку предмета в готовую рамку (пустая рамка скрывается).</summary>
    public static void SetItemImage(Border target, string? itemKey)
    {
        var img = itemKey == null ? null : Roshiq.App.Services.HeroArt.ItemIcon(itemKey);
        target.Background = img != null ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : null;
        target.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Строка с картинкой слева (портрет или иконка предмета) и обычной строкой справа.</summary>
    public static FrameworkElement WithImage(FrameworkElement image, FrameworkElement row)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (image.Margin == default) image.Margin = new Thickness(0, 2, 12, 0);
        grid.Children.Add(image);
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);
        return grid;
    }

    /// <summary>
    /// Настроить список героев: шаблон с портретом, поиск по тексту и невыбираемые заголовки атрибутов.
    /// Заголовки — обычные элементы списка (см. <see cref="WithHeaders"/>), без группировки WPF:
    /// группировка в выпадающих списках с собственным шаблоном показывала не всех героев.
    /// </summary>
    public static void HeroTemplate(ItemsControl control)
    {
        control.ItemTemplate = (DataTemplate)Application.Current.FindResource("HeroChoiceTemplate");
        TextSearch.SetTextPath(control, "Display");
        var containerType = control is ComboBox ? typeof(ComboBoxItem) : typeof(ListBoxItem);
        var style = new Style(containerType, Application.Current.TryFindResource(containerType) as Style);
        var header = new DataTrigger { Binding = new System.Windows.Data.Binding("IsHeader"), Value = true };
        header.Setters.Add(new Setter(UIElement.IsEnabledProperty, false));
        header.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        header.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Arrow));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 12, 6, 4)));
        style.Triggers.Add(header);
        control.ItemContainerStyle = style;
        ScrollViewer.SetCanContentScroll(control, false);
    }

    /// <summary>Вставить заголовок перед каждой группой атрибута. Элементы должны быть уже отсортированы по атрибуту.</summary>
    public static List<Choice<T>> WithHeaders<T>(IEnumerable<Choice<T>> items)
    {
        var list = items.ToList();
        var result = new List<Choice<T>>(list.Count + 4);
        AttributeGroup? current = null;
        foreach (var item in list)
        {
            if (item.Group != null && !item.Group.Equals(current))
            {
                current = item.Group;
                var g = current;
                // Символ нулевой ширины в начале: набор букв с клавиатуры не попадает на заголовок
                result.Add(new Choice<T>(default!, "\u200B" + g.Display)
                {
                    Group = g, IsHeader = true, GroupCount = list.Count(x => g.Equals(x.Group))
                });
            }
            result.Add(item);
        }
        return result;
    }

    /// <summary>Герои по атрибутам, внутри — по имени.</summary>
    public static IEnumerable<Hero> ByAttribute(IEnumerable<Hero> heroes, Roshiq.Core.Models.Language lang) =>
        heroes.OrderBy(h => AttributeOrder(h.Attribute)).ThenBy(h => h.DisplayName(lang), StringComparer.CurrentCultureIgnoreCase);

    public static Geometry Glyph(string key) => (Geometry)Application.Current.FindResource(key);

    private static FontFamily DisplayFont => (FontFamily)Application.Current.FindResource("DisplayFont");

    public static string Up(string s) => s.ToUpper(Loc.Instance.Language == Roshiq.Core.Models.Language.Russian
        ? new CultureInfo("ru-RU") : CultureInfo.InvariantCulture);

    public static TextBlock Text(string text, string style = "Value", double top = 0)
    {
        var tb = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 0), TextWrapping = TextWrapping.Wrap };
        tb.Style = (Style)Application.Current.FindResource(style);
        return tb;
    }

    /// <summary>Метка-«таблетка» (признак героя, статус).</summary>
    public static Border Chip(string text, string brushKey = "Panel3", string? textBrush = null)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = Res(brushKey),
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = Res(textBrush ?? "Text") }
        };
    }

    /// <summary>Строка "заголовок + пояснение" с меткой справа.</summary>
    public static FrameworkElement Row(string title, string detail, string? badge = null, string badgeBrush = "Accent")
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = Res("Text"), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(detail))
            sp.Children.Add(new TextBlock { Text = detail, Foreground = Res("Muted"), FontSize = 13.5, LineHeight = 20, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(sp);
        if (!string.IsNullOrEmpty(badge))
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderBrush = Res(badgeBrush),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(12, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = Up(badge), Foreground = Res(badgeBrush), FontSize = 11, FontWeight = FontWeights.SemiBold, FontFamily = DisplayFont }
            };
            Grid.SetColumn(b, 1);
            grid.Children.Add(b);
        }
        return grid;
    }

    /// <summary>Нумерованная строка «01  Заголовок / пояснение» в редакционном стиле.</summary>
    /// <param name="image">Картинка между номером и текстом (например <see cref="ItemIcon"/>).</param>
    public static FrameworkElement Numbered(int index, string title, string detail, string? badge = null, FrameworkElement? image = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (image != null)
        {
            Grid.SetColumn(image, 1);
            grid.Children.Add(image);
        }
        grid.Children.Add(new TextBlock
        {
            Text = index.ToString("00"),
            FontFamily = DisplayFont,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("Accent"),
            Margin = new Thickness(0, 1, 0, 0)
        });
        var row = Row(title, detail, badge);
        row.Margin = new Thickness(0);
        Grid.SetColumn(row, 2);
        grid.Children.Add(row);
        return grid;
    }

    /// <summary>Заголовок секции «01 / НАЗВАНИЕ».</summary>
    public static TextBlock Eyebrow(int index, string label)
    {
        var tb = new TextBlock { Style = (Style)Application.Current.FindResource("Eyebrow") };
        tb.Inlines.Add(new System.Windows.Documents.Run(index.ToString("00") + " / ") { Foreground = Res("Accent") });
        tb.Inlines.Add(new System.Windows.Documents.Run(Up(label)));
        return tb;
    }

    /// <summary>Блок состояния: пусто / загрузка / ошибка / нет подключения.</summary>
    public static FrameworkElement State(UiState state, string title, string text)
    {
        var (glyph, brush) = state switch
        {
            UiState.Loading => ("I.Refresh", "Accent"),
            UiState.Error => ("I.Alert", "Red"),
            UiState.Disconnected => ("I.Plug", "Warning"),
            _ => ("I.Info", "Muted")
        };
        var icon = new Icon { Glyph = Glyph(glyph), Size = 22, Brush = Res(brush) };
        if (state == UiState.Loading)
        {
            var rotate = new RotateTransform();
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = rotate;
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        var iconBox = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            Background = Res("Panel2"),
            BorderBrush = Res("Line"),
            BorderThickness = new Thickness(1),
            Child = icon,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text = Up(title),
            FontFamily = DisplayFont,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = Res("Text"),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(text))
            textPanel.Children.Add(new TextBlock { Text = text, Foreground = Res("Muted"), FontSize = 13.5, LineHeight = 20, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(iconBox, Dock.Left);
        dock.Children.Add(iconBox);
        dock.Children.Add(textPanel);
        return dock;
    }

    public static IEnumerable<Choice<Position>> Positions() =>
        Enum.GetValues<Position>().Select(p => new Choice<Position>(p, Loc.Instance.PositionName(p)));

    public static void SelectValue<T>(ComboBox box, T value)
    {
        foreach (var item in box.Items)
            if (item is Choice<T> { IsHeader: false } c && EqualityComparer<T>.Default.Equals(c.Value, value)) { box.SelectedItem = item; return; }
    }

    public static bool TryValue<T>(ComboBox box, out T value)
    {
        if (box.SelectedItem is Choice<T> { IsHeader: false } c) { value = c.Value; return true; }
        value = default!;
        return false;
    }
}
