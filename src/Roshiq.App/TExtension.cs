using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using Roshiq.Core.Localization;

namespace Roshiq.App;

/// <summary>
/// Разметочное расширение {app:T key} — живой перевод, обновляется при смене языка.
/// {app:T key, Upper=True} — то же в ВЕРХНЕМ РЕГИСТРЕ (заголовки, навигация, кнопки).
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) { Key = key; }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public bool Upper { get; set; }   // больше не меняет регистр (см. ProvideValue)

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        // 0.20.4: подписи разделов больше не капсом — обычный жирный текст читается легче.
        // Upper оставлен, чтобы не трогать разметку; заглавными остаются только значки и кнопки, где это задано в коде.
        return binding.ProvideValue(serviceProvider);
    }
}

public sealed class UpperConverter : IValueConverter
{
    public static readonly UpperConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(CultureInfo.CurrentCulture) ?? value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value;
}
