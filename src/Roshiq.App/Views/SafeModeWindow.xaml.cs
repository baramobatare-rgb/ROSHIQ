using System.Windows;
using Roshiq.App.Services;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Localization;
using Roshiq.Core.Settings;

namespace Roshiq.App.Views;

/// <summary>
/// Безопасный режим: программа несколько раз подряд закрылась при запуске. Оверлей, распознавание и фоновые
/// загрузки выключены; можно сбросить настройки, открыть диагностику или перезапуститься в обычном режиме.
/// </summary>
public partial class SafeModeWindow : Window
{
    private readonly string? _crash;

    public SafeModeWindow(int failedStarts)
    {
        InitializeComponent();
        Reason.Text = failedStarts > 0 ? Loc.Instance.Format("safe_reason", failedStarts) : Loc.T("safe_reason_manual");
        _crash = CrashReport.Latest(AppPaths.CrashReports);
        ReportButton.Visibility = _crash != null ? Visibility.Visible : Visibility.Collapsed;
        if (_crash != null) CrashReport.Acknowledge(_crash);
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => DiagnosticsRunner.Restart(safe: false);

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var backup = App.Host.Settings.ResetToDefaults();
        ThemeManager.Apply(App.Host.Settings.Current.Theme, App.Host.Settings.Current.Background);
        Status.Text = backup != null ? Loc.Instance.Format("safe_reset_done", backup) : Loc.T("safe_reset_done_nobackup");
    }

    private void Diag_Click(object sender, RoutedEventArgs e)
    {
        Close();
        DiagnosticsWindow.Open();
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_crash != null) new CrashReportWindow(_crash) { Owner = this }.ShowDialog();
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => Close();
}
