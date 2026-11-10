using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Roshiq.App.Services;
using Roshiq.Core.Localization;
using Roshiq.Core.Settings;

namespace Roshiq.App.Views;

/// <summary>Диагностика в один клик: что работает, что нет и кнопка «Исправить» рядом с проблемой.</summary>
public partial class DiagnosticsWindow : Window
{
    private IReadOnlyList<DiagnosticCheck> _checks = Array.Empty<DiagnosticCheck>();
    private readonly CancellationTokenSource _cts = new();

    public DiagnosticsWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "ROSHIQ " + AppUpdater.CurrentVersion;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>Открыть (или показать уже открытое) окно диагностики.</summary>
    public static void Open()
    {
        var existing = Application.Current.Windows.OfType<DiagnosticsWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }
        var owner = Application.Current.MainWindow;
        var w = new DiagnosticsWindow();
        if (owner != null && owner.IsVisible) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
    }

    private async Task RunAsync()
    {
        RunButton.IsEnabled = false;
        Summary.Text = Loc.T("diag_running");
        ChecksPanel.Children.Clear();
        try
        {
            _checks = await DiagnosticsRunner.RunAsync(App.Host, _cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Summary.Text = ex.Message;
            RunButton.IsEnabled = true;
            return;
        }
        int errors = _checks.Count(c => c.Status == CheckStatus.Error), warnings = _checks.Count(c => c.Status == CheckStatus.Warning);
        Summary.Text = errors + warnings == 0 ? Loc.T("diag_all_ok") : Loc.Instance.Format("diag_summary", errors, warnings);
        foreach (var c in _checks.OrderBy(c => c.Status switch { CheckStatus.Error => 0, CheckStatus.Warning => 1, CheckStatus.Ok => 2, _ => 3 }))
            ChecksPanel.Children.Add(Row(c));
        RunButton.IsEnabled = true;
    }

    private FrameworkElement Row(DiagnosticCheck c)
    {
        var brush = Ui.Res(c.Status switch
        {
            CheckStatus.Ok => "Success", CheckStatus.Warning => "Warning", CheckStatus.Error => "Red", _ => "Faint"
        });
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse { Width = 10, Height = 10, Fill = brush, Margin = new Thickness(0, 5, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(dot);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Text"), TextWrapping = TextWrapping.Wrap });
        var detail = new TextBlock { Text = c.Detail, Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        text.Children.Add(detail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (c.Fix != null && c.FixText != null)
        {
            var fix = new Button
            {
                Style = (Style)FindResource(c.Status == CheckStatus.Error ? "PrimaryButton" : "GhostButton"),
                Content = c.FixText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
            };
            fix.Click += async (_, _) =>
            {
                fix.IsEnabled = false;
                string? message;
                try { message = await c.Fix(); }
                catch (Exception ex) { message = ex.Message; }
                await RunAsync();
                if (!string.IsNullOrEmpty(message)) Summary.Text = message + " · " + Summary.Text;
            };
            Grid.SetColumn(fix, 2);
            grid.Children.Add(fix);
        }
        return new Border { Style = (Style)FindResource("InnerCard"), Child = grid, Margin = new Thickness(0, 0, 0, 8) };
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsRunner.Report(_checks));
            Summary.Text = Loc.T("diag_copied");
        }
        catch (Exception ex) { Summary.Text = ex.Message; }
    }

    private void Logs_Click(object sender, RoutedEventArgs e) => DiagnosticsRunner.OpenPath(AppPaths.Logs);

    /// <summary>«Собрать отчёт»: zip с журналами, кадрами драфта и настройками без личных данных — пользователь сам решает, кому его отправить.</summary>
    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("diag_report"),
            FileName = $"ROSHIQ-report-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            DefaultExt = ".zip",
            Filter = "ZIP|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        if (dlg.ShowDialog(this) != true) return;
        ReportButton.IsEnabled = false;
        try
        {
            var info = SystemInfo();
            var path = dlg.FileName;
            int n = await Task.Run(() => Roshiq.Core.Diagnostics.SupportReport.Create(AppPaths.DataRoot, path, info, DateTime.Now));
            Summary.Text = Loc.Instance.Format("diag_report_done", System.IO.Path.GetFileName(path), n);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex) { Summary.Text = Loc.T("diag_report_failed") + " " + ex.Message; }
        finally { ReportButton.IsEnabled = true; }
    }

    /// <summary>Сведения о системе для отчёта: версия, Windows, экраны и масштаб, окно игры, оверлей.</summary>
    internal static string SystemInfo()
    {
        var h = App.Host;
        var s = h.Settings.Current;
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? new Window());
        var g = h.Overlay.Game;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ROSHIQ {AppUpdater.CurrentVersion}");
        sb.AppendLine($"Создан: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Windows: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}; .NET {Environment.Version}");
        sb.AppendLine($"Основной экран: {SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX:0}×{SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY:0}, масштаб Windows {dpi.DpiScaleX:P0}");
        sb.AppendLine($"Все экраны: {SystemParameters.VirtualScreenWidth * dpi.DpiScaleX:0}×{SystemParameters.VirtualScreenHeight * dpi.DpiScaleY:0}");
        sb.AppendLine(g.Found ? $"Окно Dota 2: {g.ClientArea.Width}×{g.ClientArea.Height}, {(g.Foreground ? "активно" : "не активно")}{(g.Minimized ? ", свёрнуто" : "")}" : "Окно Dota 2: не найдено");
        sb.AppendLine($"Оверлей: режим {s.Overlay.Mode}, масштаб {s.Overlay.Scale:0.##}, автомасштаб {(s.Overlay.AutoScale ? h.Overlay.AutoScale.ToString("0.##") : "выкл")}, итог {h.Overlay.EffectiveScale:0.##}");
        sb.AppendLine($"Язык {s.Language}, роль матча {h.CurrentPosition} ({h.Roles.Source}), обычная роль {s.Position}, режим {s.UsageContext}, тема {s.Theme}/{s.Background}, масштаб окна {s.UiScale:0.##}");
        sb.AppendLine($"GSI: {(s.Gsi.Enabled ? "включён" : "выключен")}, порт {s.Gsi.Port}; автодрафт {(s.Vision.AutoDraftEnabled ? "включён" : "выключен")}");
        sb.AppendLine($"Steam привязан: {(h.SteamAccount != null ? "да" : "нет")}; сеть для статистики: {(s.Data.AllowOnlineStats ? "разрешена" : "запрещена")}");
        sb.AppendLine($"Матчей в истории: {h.History.All().Count}");
        return sb.ToString();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
