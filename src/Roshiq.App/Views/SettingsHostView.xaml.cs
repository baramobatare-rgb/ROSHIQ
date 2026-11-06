using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Roshiq.App.Views;

/// <summary>05 / Настройки: общие (язык, тема, масштаб, режим, интеграция), оверлей, данные и обновления.</summary>
public partial class SettingsHostView : UserControl, IRefreshable
{
    private readonly Dictionary<string, UserControl> _tabs = new();

    public SettingsHostView()
    {
        InitializeComponent();
        TabGeneral.IsChecked = true;
    }

    public void Refresh()
    {
        if (TabHost.Content is IRefreshable r) r.Refresh();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string key }) return;
        if (!_tabs.TryGetValue(key, out var view))
        {
            view = key switch
            {
                "overlay" => new OverlaySettingsView(),
                "data" => new UpdatesView(),
                _ => new SettingsView()
            };
            _tabs[key] = view;
        }
        TabHost.Content = view;
        if (view is IRefreshable r) r.Refresh();
        Motion.Enter(TabHost, 0, 10);
    }
}
