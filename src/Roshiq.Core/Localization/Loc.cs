using System.ComponentModel;
using Roshiq.Core.Models;

namespace Roshiq.Core.Localization;

/// <summary>
/// Локализация RU/EN. В XAML используется через индексатор: {Binding [key], Source={x:Static loc:Loc.Instance}}
/// При смене языка все привязки обновляются (PropertyChanged "Item[]").
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private Language _language = Language.Russian;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Language Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (Strings.Table.TryGetValue(key, out var pair) || StringsExtra.Table.TryGetValue(key, out pair))
            return _language == Language.Russian ? pair.Ru : pair.En;
        return key;
    }

    public string Format(string key, params object[] args)
    {
        try { return string.Format(Get(key), args); }
        catch (FormatException) { return Get(key); }
    }

    public static string T(string key) => Instance.Get(key);

    public string PositionName(Position p) => Get("pos_" + (int)p);

    public string PhaseName(BuildPhase phase) => Get("phase_" + phase.ToString().ToLowerInvariant());

    public string ConfidenceName(Confidence c) => Get("conf_" + c.ToString().ToLowerInvariant());
}
