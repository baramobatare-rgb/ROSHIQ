using System.Windows;
using Roshiq.App.Services;
using Roshiq.Core.Localization;

namespace Roshiq.App.Views;

/// <summary>Окно «ROSHIQ закрылся с ошибкой»: текст отчёта, копирование и issue на GitHub.</summary>
public partial class CrashReportWindow : Window
{
    private readonly string _path;

    public CrashReportWindow(string reportPath)
    {
        InitializeComponent();
        _path = reportPath;
        try { ReportText.Text = File.ReadAllText(reportPath); }
        catch (Exception ex) { ReportText.Text = ex.Message; }
    }

    private bool CopyReport()
    {
        try { Clipboard.SetText(ReportText.Text); return true; }
        catch { return false; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) =>
        Status.Text = CopyReport() ? Loc.T("diag_copied") : "";

    /// <summary>Новое issue на GitHub: отчёт уже в буфере обмена — останется вставить (Ctrl+V).</summary>
    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        CopyReport();
        var repo = App.Host.Settings.Current.Data.UpdateRepo;
        if (string.IsNullOrWhiteSpace(repo)) repo = "baramobatare-rgb/ROSHIQ";
        var title = Uri.EscapeDataString($"Сбой ROSHIQ {AppUpdater.CurrentVersion}");
        var body = Uri.EscapeDataString(Loc.T("crash_issue_body"));
        DiagnosticsRunner.OpenPath($"https://github.com/{repo}/issues/new?title={title}&body={body}");
        Status.Text = Loc.T("crash_github_hint");
    }

    private void Folder_Click(object sender, RoutedEventArgs e) => DiagnosticsRunner.OpenPath(Path.GetDirectoryName(_path)!);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
