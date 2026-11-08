using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Roshiq.App.Services;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Localization;
using Roshiq.Core.Settings;
using Roshiq.Data;

namespace Roshiq.App.Views;

public partial class UpdatesView : UserControl, IRefreshable
{
    private static string HistoryPath => Path.Combine(AppPaths.DataRoot, "update_history.log");
    private CancellationTokenSource? _cts;
    private bool _ready;

    public UpdatesView()
    {
        InitializeComponent();
    }

    public void Refresh()
    {
        _ready = false;
        var L = Loc.Instance;
        var db = App.Host.Database;
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
        DataInfo.Text = L.Format("upd_data_line", version, db.DataVersion, db.PatchBaseline, db.Heroes.Count, db.Items.Count,
            db.CounterRules.Count + db.SynergyRules.Count);
        DataWarnings.Text = (db.OverriddenFiles.Count > 0 ? L.Format("upd_overrides", string.Join(", ", db.OverriddenFiles)) + "\n" : "") +
                            (db.Warnings.Count == 0 ? L.Get("upd_no_warnings") : L.Format("upd_warnings", db.Warnings.Count) + "\n" + string.Join("\n", db.Warnings.Take(8)));

        var stats = App.Host.Stats;
        StatsInfo.Text = stats.HeroesCached == 0
            ? L.Get("upd_stats_empty")
            : L.Format("upd_stats_line", stats.HeroesCached, stats.LastUpdatedUtc?.ToLocalTime().ToString("g") ?? "—");
        var d = App.Host.Settings.Current.Data;
        AllowOnline.IsChecked = d.AllowOnlineStats;
        ApiKey.Password = d.OpenDotaApiKey ?? "";
        MinGames.Text = d.MinGamesForStats.ToString();
        RefreshStats.IsEnabled = d.AllowOnlineStats && _cts == null;

        History.Text = File.Exists(HistoryPath) ? string.Join(Environment.NewLine, File.ReadLines(HistoryPath).Reverse().Take(50)) : "";
        Network.Text = string.Join(Environment.NewLine, NetworkLog.All.Reverse());
        RefreshOnline();
        RefreshFreshness();
        _ready = true;
    }

    /// <summary>Откуда взяты данные, что в них устарело и как обновляются.</summary>
    private void RefreshFreshness()
    {
        var L = Loc.Instance;
        var h = App.Host;
        var db = h.Database;
        var d = h.Settings.Current.Data;
        var source = db.RemoteFiles.Count > 0
            ? L.Format("fresh_source_remote", db.DataVersion, db.RemoteFiles.Count)
            : L.Format("fresh_source_builtin", db.DataVersion);
        if (db.OverriddenFiles.Count > 0) source += " · " + L.Format("fresh_source_mine", db.OverriddenFiles.Count);
        FreshSource.Text = source;

        FreshAudit.Children.Clear();
        if (h.Audit is not { } a)
            FreshAudit.Children.Add(Ui.Text(L.Get(d.AllowOnlineStats ? "fresh_not_checked" : "steam_need_network"), "MutedText"));
        else
        {
            FreshAudit.Children.Add(Ui.Row(L.Get("fresh_patch"),
                a.PatchBehind ? L.Format("fresh_patch_behind", a.LatestPatch ?? "?", a.Baseline) : L.Format("fresh_patch_ok", a.LatestPatch ?? a.Baseline),
                a.PatchBehind ? "!" : "✓", a.PatchBehind ? "Warning" : "Accent"));
            FreshAudit.Children.Add(Ui.Row(L.Get("fresh_heroes"),
                a.NewHeroes.Count == 0 ? L.Get("fresh_heroes_ok") : L.Format("fresh_heroes_new", string.Join(", ", a.NewHeroes)),
                a.NewHeroes.Count == 0 ? "✓" : a.NewHeroes.Count.ToString(), a.NewHeroes.Count == 0 ? "Accent" : "Warning"));
            FreshAudit.Children.Add(Ui.Row(L.Get("fresh_items"),
                a.RemovedItems.Count == 0 ? L.Get("fresh_items_ok") : L.Format("fresh_items_removed", string.Join(", ", a.RemovedItems.Take(12))),
                a.RemovedItems.Count == 0 ? "✓" : a.RemovedItems.Count.ToString(), a.RemovedItems.Count == 0 ? "Accent" : "Warning"));
            if (a.HasIssues) FreshAudit.Children.Add(Ui.Text(L.Get("fresh_issues_hint"), "MutedText", 4));
        }
        FreshMessage.Text = string.Join(" · ", new[]
        {
            h.DataUpdateMessage,
            d.DataCheckedUtc is DateTime c ? L.Format("fresh_checked_at", c.ToLocalTime().ToString("dd.MM HH:mm")) : null,
            h.Stats.LastUpdatedUtc is DateTime st ? L.Format("fresh_matchups_at", st.ToLocalTime().ToString("dd.MM")) : null
        }.Where(x => !string.IsNullOrEmpty(x)));
        AutoDataBox.IsChecked = d.AutoUpdateData;
        AutoMatchupsBox.IsChecked = d.AutoRefreshMatchups;
        FreshCheckButton.IsEnabled = d.AllowOnlineStats;
        FreshResetButton.IsEnabled = db.RemoteFiles.Count > 0;
    }

    private void Fresh_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s =>
        {
            s.Data.AutoUpdateData = AutoDataBox.IsChecked == true;
            s.Data.AutoRefreshMatchups = AutoMatchupsBox.IsChecked == true;
        });
    }

    private async void FreshCheck_Click(object sender, RoutedEventArgs e)
    {
        FreshCheckButton.IsEnabled = false;
        FreshMessage.Text = Loc.T("fresh_checking");
        try
        {
            var error = await App.Host.RunOnlineChecksAsync(force: true);
            Refresh();
            if (error != null) FreshMessage.Text += " · " + error;
        }
        finally { FreshCheckButton.IsEnabled = App.Host.Settings.Current.Data.AllowOnlineStats; }
    }

    private void FreshReset_Click(object sender, RoutedEventArgs e)
    {
        App.Host.ResetRemoteData();
        Refresh();
    }

    /// <summary>Патч, цены предметов и версия ROSHIQ.</summary>
    private void RefreshOnline()
    {
        var L = Loc.Instance;
        var h = App.Host;
        var d = h.Settings.Current.Data;
        var latest = h.Patch.Latest;
        PatchInfo.Text = latest == null
            ? L.Format("upd_patch_unknown", h.Database.PatchBaseline)
            : PatchVersion.IsNewer(latest, h.Database.PatchBaseline)
                ? L.Format("upd_patch_new", latest, PatchVersion.Short(h.Database.PatchBaseline) ?? h.Database.PatchBaseline)
                : L.Format("upd_patch_ok", latest);
        AckPatch.Visibility = h.NewPatchNotice != null ? Visibility.Visible : Visibility.Collapsed;
        CostInfo.Text = h.Costs.Count == 0
            ? L.Get("upd_costs_none")
            : L.Format("upd_costs_line", h.Costs.Count, h.Costs.UpdatedUtc?.ToLocalTime().ToString("g") ?? "—");
        AutoCheck.IsChecked = d.AutoCheckOnline;
        CheckOnline.IsEnabled = d.AllowOnlineStats;
        if (!d.AllowOnlineStats) OnlineStatus.Text = L.Get("upd_need_online");

        AppVersionInfo.Text = L.Format("upd_app_version", AppUpdater.CurrentVersion.ToString(3));
        if (!RepoBox.IsKeyboardFocusWithin) RepoBox.Text = d.UpdateRepo ?? "";
        CheckApp.IsEnabled = d.AllowOnlineStats && !string.IsNullOrWhiteSpace(d.UpdateRepo);
        var upd = h.AvailableUpdate;
        InstallApp.IsEnabled = upd?.InstallerUrl != null && _downloadCts == null;
        OpenRelease.IsEnabled = upd != null;
        if (upd != null && _downloadCts == null) AppStatus.Text = L.Format("upd_app_available", upd.Tag);
        ShowPatchNotes();
    }

    /// <summary>Изменения последнего патча только для героев из пула (избранные) и текущего героя.</summary>
    private void ShowPatchNotes()
    {
        var L = Loc.Instance;
        var h = App.Host;
        PatchNotesPanel.Children.Clear();
        var notes = h.PatchNotes;
        if (notes == null) { PatchNotesInfo.Text = L.Get("pn_none"); return; }
        var keys = h.Settings.Current.FavoriteHeroKeys.ToList();
        if (h.CurrentHero != null && !keys.Contains(h.CurrentHero.Key, StringComparer.OrdinalIgnoreCase)) keys.Insert(0, h.CurrentHero.Key);
        if (keys.Count == 0) { PatchNotesInfo.Text = L.Format("pn_no_pool", notes.Patch); return; }
        int shown = 0;
        foreach (var key in keys)
        {
            var hero = h.Database.FindHero(key);
            if (hero == null || !notes.Heroes.TryGetValue(hero.Key, out var list) || list.Count == 0) continue;
            shown++;
            PatchNotesPanel.Children.Add(Ui.Text(Ui.Up(hero.DisplayName(L.Language)), "Eyebrow", shown == 1 ? 0 : 10));
            foreach (var line in list.Take(6)) PatchNotesPanel.Children.Add(Ui.Text("• " + line, "MutedText", 2));
        }
        PatchNotesInfo.Text = shown == 0 ? L.Format("pn_untouched", notes.Patch, keys.Count) : L.Format("pn_info", notes.Patch, shown, keys.Count);
    }

    private CancellationTokenSource? _downloadCts;

    private void AutoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s => s.Data.AutoCheckOnline = AutoCheck.IsChecked == true);
    }

    private async void CheckOnline_Click(object sender, RoutedEventArgs e)
    {
        CheckOnline.IsEnabled = false;
        OnlineStatus.Text = Loc.T("upd_checking");
        var error = await App.Host.RunOnlineChecksAsync(force: true);
        OnlineStatus.Text = error == null ? Loc.T("upd_check_done") : Loc.Instance.Format("upd_check_error", error);
        AppendHistory(error == null ? "Проверка патча и цен: OK" : "Проверка патча и цен: " + error);
        Refresh();
    }

    private void AckPatch_Click(object sender, RoutedEventArgs e)
    {
        App.Host.AcknowledgePatch();
        Refresh();
    }

    private void SaveRepo_Click(object sender, RoutedEventArgs e)
    {
        var text = RepoBox.Text.Trim();
        var repo = text.Length == 0 ? null : AppUpdater.NormalizeRepo(text);
        if (text.Length > 0 && repo == null) { AppStatus.Text = Loc.T("upd_repo_invalid"); return; }
        App.Host.Settings.Update(s => s.Data.UpdateRepo = repo);
        RepoBox.Text = repo ?? "";
        AppStatus.Text = Loc.T("saved");
        Refresh();
    }

    private async void CheckApp_Click(object sender, RoutedEventArgs e)
    {
        var repo = App.Host.Settings.Current.Data.UpdateRepo;
        if (string.IsNullOrWhiteSpace(repo)) return;
        CheckApp.IsEnabled = false;
        AppStatus.Text = Loc.T("upd_checking");
        try
        {
            var result = await App.Host.CheckForUpdateAsync(manual: true);
            AppStatus.Text = AppUpdater.Describe(result);
        }
        catch (Exception ex) { AppStatus.Text = Loc.Instance.Format("upd_check_error", ex.Message); }
        finally { CheckApp.IsEnabled = true; }
        RefreshOnline();
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        if (App.Host.AvailableUpdate is { } upd) AppUpdater.OpenPage(upd.PageUrl);
    }

    private async void InstallApp_Click(object sender, RoutedEventArgs e)
    {
        var upd = App.Host.AvailableUpdate;
        if (upd?.InstallerUrl == null || _downloadCts != null) return;
        var sizeMb = upd.InstallerSize > 0 ? (upd.InstallerSize / 1048576.0).ToString("0.0") : "?";
        bool silent = AppUpdater.CanUpdateSilently;
        if (MessageBox.Show(Loc.Instance.Format(silent ? "upd_app_update_confirm" : "upd_app_download_confirm", upd.InstallerName ?? "setup.exe", sizeMb, upd.Tag), "ROSHIQ",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _downloadCts = new CancellationTokenSource();
        InstallApp.IsEnabled = false;
        AppProgress.Visibility = Visibility.Visible;
        AppProgress.Value = 0;
        try
        {
            var path = await AppUpdater.DownloadAsync(upd, new Progress<double>(p => AppProgress.Value = p), _downloadCts.Token);
            AppendHistory($"Скачано обновление ROSHIQ {upd.Tag}");
            if (silent || MessageBox.Show(Loc.T("upd_app_run_confirm"), "ROSHIQ", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                AppUpdater.RunInstaller(path);
                if (Application.Current.MainWindow is MainWindow main) main.RequestExit();
                else Application.Current.Shutdown();
                return;
            }
            AppStatus.Text = Loc.Instance.Format("upd_app_saved", path);
        }
        catch (Exception ex) { AppStatus.Text = Loc.Instance.Format("upd_check_error", ex.Message); }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            AppProgress.Visibility = Visibility.Collapsed;
            RefreshOnline();
        }
    }

    private static void AppendHistory(string line)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.AppendAllText(HistoryPath, $"{DateTime.Now:yyyy-MM-dd HH:mm} {line}{Environment.NewLine}");
        }
        catch (Exception ex) { Log.Warn("История обновлений: " + ex.Message); }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        App.Host.ReloadDatabase();
        AppendHistory($"Перезагрузка базы: {App.Host.Database.DataVersion}, предупреждений {App.Host.Database.Warnings.Count}");
        Refresh();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataOverrides);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataOverrides}\"") { UseShellExecute = true }); }
        catch (Exception ex) { StatsStatus.Text = ex.Message; }
    }

    /// <summary>Выгрузить встроенные файлы базы в папку переопределений, чтобы их можно было отредактировать.</summary>
    private void ExportData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataOverrides);
        var asm = typeof(GameDatabase).Assembly;
        int n = 0;
        foreach (var name in asm.GetManifestResourceNames().Where(r => r.StartsWith("Roshiq.Data.") && r.EndsWith(".txt")))
        {
            var file = name["Roshiq.Data.".Length..];
            var target = Path.Combine(AppPaths.DataOverrides, file + ".default");
            using var s = asm.GetManifestResourceStream(name)!;
            using var f = File.Create(target);
            s.CopyTo(f);
            n++;
        }
        StatsStatus.Text = Loc.Instance.Format("upd_exported", n, AppPaths.DataOverrides);
    }

    private void AllowOnline_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        App.Host.Settings.Update(s => s.Data.AllowOnlineStats = AllowOnline.IsChecked == true);
        RefreshStats.IsEnabled = AllowOnline.IsChecked == true && _cts == null;
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Settings.Update(s =>
        {
            s.Data.OpenDotaApiKey = string.IsNullOrWhiteSpace(ApiKey.Password) ? null : ApiKey.Password.Trim();
            if (int.TryParse(MinGames.Text.Trim(), out var g)) s.Data.MinGamesForStats = Math.Clamp(g, 20, 100000);
        });
        App.Host.RebuildEngines();
        StatsStatus.Text = Loc.T("saved");
    }

    private async void RefreshStats_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null || !App.Host.Settings.Current.Data.AllowOnlineStats) return;
        _cts = new CancellationTokenSource();
        StatsState.Content = Ui.State(UiState.Loading, Loc.T("state_loading_stats_title"), Loc.T("upd_stats_hint"));
        RefreshStats.IsEnabled = false;
        CancelStats.IsEnabled = true;
        var ids = App.Host.Database.Heroes.Select(h => h.Id).ToList();
        int done = 0;
        bool failed = false;
        var progress = new Progress<string>(msg =>
        {
            done++;
            StatsProgress.Value = (double)done / ids.Count;
            StatsStatus.Text = $"{done}/{ids.Count} · {msg}";
        });
        try
        {
            using var client = new OpenDotaClient(App.Host.Settings.Current.Data.OpenDotaApiKey);
            int ok = await client.RefreshAsync(App.Host.Stats, ids, progress, _cts.Token);
            AppendHistory($"Статистика OpenDota обновлена: {ok}/{ids.Count} героев");
            StatsStatus.Text = Loc.Instance.Format("upd_stats_done", ok, ids.Count);
        }
        catch (OperationCanceledException)
        {
            StatsStatus.Text = Loc.T("upd_cancelled");
        }
        catch (Exception ex)
        {
            StatsStatus.Text = ex.Message;
            failed = true;
            StatsState.Content = Ui.State(UiState.Error, Loc.T("state_stats_error_title"), ex.Message);
        }
        finally
        {
            if (!failed) StatsState.Content = null;
            _cts.Dispose();
            _cts = null;
            CancelStats.IsEnabled = false;
            App.Host.RebuildEngines();
            Refresh();
        }
    }

    private void CancelStats_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ClearStats_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Stats.Clear();
        App.Host.RebuildEngines();
        AppendHistory("Кэш статистики очищен");
        Refresh();
    }
}
