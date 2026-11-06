using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Roshiq.Overlay;

public sealed class OverlayRow
{
    public OverlayRow(string title, string detail, string? badge = null)
    {
        Title = title;
        Detail = detail;
        Badge = badge ?? "";
    }

    public string Title { get; }
    public string Detail { get; }
    /// <summary>Иконка предмета или портрет героя слева. null — без картинки.</summary>
    public System.Windows.Media.ImageSource? Icon { get; init; }
    public string Badge { get; }
}

/// <summary>Данные, которые показывает оверлей. Заполняется приложением, отображается окнами оверлея.</summary>
public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private string _header = "ROSHIQ";
    private string _subHeader = "";
    private string _mainTitle = "";
    private string _mainDetail = "";
    private string _footer = "";
    private bool _expanded;
    private bool _draftMode;
    private bool _editMode;
    private string _section1Title = "";
    private string _section2Title = "";
    private string _hideHint = "F8";
    private string _detailsHint = "CTRL+F8";
    private string _timersLine = "";
    private System.Windows.Media.ImageSource? _heroPortrait;
    private double _goldProgress = -1;
    private bool _timersUrgent;
    private bool _animate = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Header { get => _header; set => Set(ref _header, value); }
    public string SubHeader { get => _subHeader; set => Set(ref _subHeader, value); }
    /// <summary>Главная рекомендация (карточка Minimal).</summary>
    public string MainTitle { get => _mainTitle; set => Set(ref _mainTitle, value); }
    public string MainDetail { get => _mainDetail; set => Set(ref _mainDetail, value); }
    public string Footer { get => _footer; set => Set(ref _footer, value); }
    public bool DraftMode { get => _draftMode; set => Set(ref _draftMode, value); }
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
    public bool EditMode { get => _editMode; set => Set(ref _editMode, value); }
    public string Section1Title { get => _section1Title; set => Set(ref _section1Title, value); }
    public string Section2Title { get => _section2Title; set => Set(ref _section2Title, value); }
    /// <summary>Подсказки горячих клавиш (оверлей не перехватывает мышь, поэтому управление — с клавиатуры).</summary>
    public string HideHint { get => _hideHint; set => Set(ref _hideHint, value); }
    public string DetailsHint { get => _detailsHint; set => Set(ref _detailsHint, value); }
    /// <summary>Строка ближайших событий матча: «РУНА СИЛЫ 0:24 · РОШАН 7:10–10:10». Пусто — строка скрыта.</summary>
    public string TimersLine { get => _timersLine; set => Set(ref _timersLine, value); }
    /// <summary>Портрет героя у главной строки (текущий герой или лучший контрпик).</summary>
    public System.Windows.Media.ImageSource? HeroPortrait { get => _heroPortrait; set => Set(ref _heroPortrait, value); }
    /// <summary>Доля золота до следующего предмета (0…1); меньше 0 — полоска скрыта.</summary>
    public double GoldProgress { get => _goldProgress; set => Set(ref _goldProgress, value); }
    /// <summary>До события меньше 10 секунд — строка таймеров подсвечивается и «дышит».</summary>
    public bool TimersUrgent { get => _timersUrgent; set => Set(ref _timersUrgent, value); }
    /// <summary>Анимации разрешены (нет «Уменьшить анимацию»).</summary>
    public bool Animate { get => _animate; set => Set(ref _animate, value); }

    public ObservableCollection<OverlayRow> Section1 { get; } = new();
    public ObservableCollection<OverlayRow> Section2 { get; } = new();

    public void ReplaceRows(ObservableCollection<OverlayRow> target, IEnumerable<OverlayRow> rows)
    {
        // Оверлей обновляется раз в секунду: не пересоздаём строки, если содержимое не изменилось (без мерцания и лишней отрисовки).
        var list = rows.ToList();
        if (list.Count == target.Count && list.Zip(target).All(p =>
                p.First.Title == p.Second.Title && p.First.Detail == p.Second.Detail && p.First.Badge == p.Second.Badge
                && ReferenceEquals(p.First.Icon, p.Second.Icon)))
            return;
        target.Clear();
        foreach (var r in list) target.Add(r);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
