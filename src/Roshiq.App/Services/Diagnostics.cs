using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Localization;
using Roshiq.Core.Settings;
using Roshiq.GSI;

namespace Roshiq.App.Services;

public enum CheckStatus { Ok, Warning, Error, Info }

/// <summary>Одна проверка диагностики. Fix — «Исправить» (необязательно); после него проверки запускаются заново.</summary>
public sealed record DiagnosticCheck(CheckStatus Status, string Title, string Detail, string? FixText = null, Func<Task<string?>>? Fix = null);

/// <summary>
/// Диагностика «почему не работает»: Dota, файл связи с игрой, порт, данные GSI, параметр запуска,
/// горячие клавиши, папка данных, интернет. Только чтение; исправления — по кнопке пользователя.
/// </summary>
public static class DiagnosticsRunner
{
    public static async Task<IReadOnlyList<DiagnosticCheck>> RunAsync(AppHost host, CancellationToken ct)
    {
        var L = Loc.Instance;
        var s = host.Settings.Current;
        var list = new List<DiagnosticCheck>();

        if (host.SafeMode)
            list.Add(new(CheckStatus.Warning, L.Get("diag_safe"), L.Get("diag_safe_detail"), L.Get("diag_fix_restart"),
                () => { Restart(safe: false); return Task.FromResult<string?>(null); }));

        // 1. Dota 2
        var root = SteamLocator.FindDotaRoot();
        bool dotaRunning = Process.GetProcessesByName("dota2").Length > 0;
        list.Add(root == null
            ? new(CheckStatus.Error, L.Get("diag_dota"), L.Get("diag_dota_missing"))
            : new(CheckStatus.Ok, L.Get("diag_dota"), root + (dotaRunning ? " · " + L.Get("diag_dota_running") : "")));

        // 2. GSI включён
        var g = s.Gsi;
        if (!g.Enabled)
            list.Add(new(CheckStatus.Error, L.Get("diag_gsi_enabled"), L.Get("diag_gsi_disabled"), L.Get("diag_fix_enable"),
                () => { host.Settings.Update(x => x.Gsi.Enabled = true); return Task.FromResult<string?>(null); }));

        // 3. Файл связи с игрой
        if (root != null)
        {
            string expected = GsiConfigWriter.BuildContent(g.Port, g.Token);
            string? actual = null;
            try { if (GsiConfigWriter.Exists(root)) actual = File.ReadAllText(GsiConfigWriter.TargetPath(root)); } catch { }
            Func<Task<string?>> write = () =>
            {
                try { GsiConfigWriter.Write(root, g.Port, g.Token); return Task.FromResult<string?>(L.Get("diag_cfg_written")); }
                catch (Exception ex) { return Task.FromResult<string?>(ex.Message); }
            };
            if (actual == null)
                list.Add(new(CheckStatus.Error, L.Get("diag_cfg"), L.Get("diag_cfg_missing"), L.Get("diag_fix_write"), write));
            else if (Normalize(actual) != Normalize(expected))
                list.Add(new(CheckStatus.Error, L.Get("diag_cfg"), L.Get("diag_cfg_outdated"), L.Get("diag_fix_write"), write));
            else
                list.Add(new(CheckStatus.Ok, L.Get("diag_cfg"), L.Get("diag_cfg_ok")));
        }

        // 4. Приёмник данных (порт)
        if (g.Enabled)
        {
            var gsi = host.Gsi;
            if (gsi == null || !gsi.IsRunning)
                list.Add(new(CheckStatus.Error, L.Get("diag_port"), L.Format("diag_port_busy", g.Port, gsi?.LastError ?? "?"), L.Get("diag_fix_port"),
                    () =>
                    {
                        int port = FreePort(g.Port + 1);
                        host.Settings.Update(x => x.Gsi.Port = port);
                        if (root != null) try { GsiConfigWriter.Write(root, port, g.Token); } catch { }
                        return Task.FromResult<string?>(L.Format("diag_port_changed", port));
                    }));
            else
                list.Add(new(CheckStatus.Ok, L.Get("diag_port"), L.Format("diag_port_ok", g.Port)));

            // 5. Данные из игры
            if (gsi != null && gsi.IsRunning)
            {
                if (gsi.LastPacketUtc is DateTime last)
                {
                    var ago = DateTime.UtcNow - last;
                    bool fresh = ago < TimeSpan.FromSeconds(45);
                    list.Add(new(fresh || !dotaRunning ? CheckStatus.Ok : CheckStatus.Warning, L.Get("diag_data"),
                        L.Format("diag_data_last", Ago(ago), gsi.PacketsReceived)));
                }
                else if (gsi.PacketsRejected > 0)
                    list.Add(new(CheckStatus.Error, L.Get("diag_data"), L.Format("diag_data_rejected", gsi.PacketsRejected), L.Get("diag_fix_write"),
                        () =>
                        {
                            if (root == null) return Task.FromResult<string?>(L.Get("diag_dota_missing"));
                            GsiConfigWriter.Write(root, g.Port, g.Token);
                            return Task.FromResult<string?>(L.Get("diag_cfg_written"));
                        }));
                else
                    list.Add(new(dotaRunning ? CheckStatus.Error : CheckStatus.Info, L.Get("diag_data"),
                        L.Get(dotaRunning ? "diag_data_none_running" : "diag_data_none")));
            }
        }

        // 6. Параметр запуска
        var flag = DotaLaunchOptions.CheckInstalled();
        list.Add(flag switch
        {
            true => new(CheckStatus.Ok, L.Get("diag_launch"), L.Get("diag_launch_ok")),
            false => new(CheckStatus.Warning, L.Get("diag_launch"), L.Get("diag_launch_missing"), L.Get("diag_fix_copy_flag"),
                () =>
                {
                    try { System.Windows.Clipboard.SetText(DotaLaunchOptions.GsiFlag); } catch { }
                    return Task.FromResult<string?>(L.Get("diag_launch_copied"));
                }),
            _ => new(CheckStatus.Info, L.Get("diag_launch"), L.Get("diag_launch_unknown"))
        });

        // 7. Горячие клавиши
        var failed = host.Overlay.FailedHotkeys;
        if (host.SafeMode || !host.Overlay.HotkeysRegistered)
            list.Add(new(CheckStatus.Info, L.Get("diag_hotkeys"), L.Get("diag_hotkeys_off")));
        else if (failed.Count > 0)
            list.Add(new(CheckStatus.Warning, L.Get("diag_hotkeys"), L.Format("diag_hotkeys_busy", string.Join(", ", failed)), L.Get("diag_fix_hotkeys"),
                () => { MainWindow.Navigate("settings/overlay"); return Task.FromResult<string?>(null); }));
        else
            list.Add(new(CheckStatus.Ok, L.Get("diag_hotkeys"), L.Get("diag_hotkeys_ok")));

        // 8. Папка данных
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            var probe = Path.Combine(AppPaths.DataRoot, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            list.Add(new(CheckStatus.Ok, L.Get("diag_folder"), AppPaths.DataRoot));
        }
        catch (Exception ex)
        {
            list.Add(new(CheckStatus.Error, L.Get("diag_folder"), L.Format("diag_folder_readonly", AppPaths.DataRoot, ex.Message)));
        }

        // 9. Ошибки
        var crash = CrashReport.Latest(AppPaths.CrashReports);
        var error = CrashReport.Latest(AppPaths.CrashReports, CrashReport.ErrorPrefix);
        var newest = new[] { crash, error }.Where(p => p != null).Select(p => p!).OrderByDescending(p => File.GetLastWriteTime(p)).FirstOrDefault();
        if (newest != null && DateTime.Now - File.GetLastWriteTime(newest) < TimeSpan.FromDays(7))
            list.Add(new(CheckStatus.Warning, L.Get("diag_errors"), L.Format("diag_errors_recent", File.GetLastWriteTime(newest).ToString("g", CultureInfo.CurrentCulture)),
                L.Get("diag_fix_show"), () => { OpenPath(newest); return Task.FromResult<string?>(null); }));
        else
            list.Add(new(CheckStatus.Ok, L.Get("diag_errors"), L.Get("diag_errors_none")));

        // 10. Интернет
        if (!s.Data.AllowOnlineStats)
            list.Add(new(CheckStatus.Info, L.Get("diag_net"), L.Get("diag_net_off")));
        else
        {
            var od = await PingAsync("https://api.opendota.com/api/constants/patch", ct);
            list.Add(od == null
                ? new(CheckStatus.Ok, "OpenDota", L.Get("diag_net_ok"))
                : new(CheckStatus.Warning, "OpenDota", L.Format("diag_net_fail", od)));
            var gh = await PingAsync(Roshiq.Data.RemoteData.BaseUrl(string.IsNullOrWhiteSpace(s.Data.UpdateRepo) ? "baramobatare-rgb/ROSHIQ" : s.Data.UpdateRepo) + "data_version.txt", ct);
            list.Add(gh == null
                ? new(CheckStatus.Ok, "GitHub", L.Get("diag_net_ok"))
                : new(CheckStatus.Warning, "GitHub", L.Format("diag_net_fail", gh)));
        }
        return list;
    }

    /// <summary>Текст для «Скопировать отчёт»: версия, проверки и последние строки журнала.</summary>
    public static string Report(IEnumerable<DiagnosticCheck> checks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ROSHIQ {AppUpdater.CurrentVersion} · Windows {Environment.OSVersion.Version} · {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        foreach (var c in checks)
            sb.AppendLine($"[{c.Status switch { CheckStatus.Ok => "OK", CheckStatus.Warning => "!!", CheckStatus.Error => "XX", _ => "--" }}] {c.Title}: {c.Detail}");
        sb.AppendLine();
        sb.AppendLine("Журнал:");
        foreach (var line in Log.RecentLines.TakeLast(30)) sb.AppendLine(line);
        return sb.ToString();
    }

    /// <summary>Перезапустить программу (в обычном или безопасном режиме).</summary>
    public static void Restart(bool safe)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            if (!safe) CrashGuard.MarkStable(AppPaths.StartupGuard);
            // новая копия ждёт, пока эта закроется (защита от второго экземпляра)
            var args = safe ? "--safe-mode" : "";
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\" {args}")
            {
                UseShellExecute = false, CreateNoWindow = true
            });
            (System.Windows.Application.Current.MainWindow as MainWindow)?.RequestExit();
        }
        catch (Exception ex) { Log.Error("Перезапуск", ex); }
    }

    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(Directory.Exists(path)
                ? new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }
                : new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("Не удалось открыть " + path + ": " + ex.Message); }
    }

    private static string Normalize(string text) => text.Replace("\r", "").Trim();

    private static string Ago(TimeSpan t) =>
        t.TotalSeconds < 90 ? Loc.Instance.Format("diag_ago_sec", (int)t.TotalSeconds)
        : t.TotalMinutes < 90 ? Loc.Instance.Format("diag_ago_min", (int)t.TotalMinutes)
        : Loc.Instance.Format("diag_ago_hours", (int)t.TotalHours);

    private static int FreePort(int start)
    {
        for (int p = start; p < start + 50; p++)
        {
            try
            {
                var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch { }
        }
        return start;
    }

    /// <summary>null — сервер ответил; иначе текст ошибки.</summary>
    private static async Task<string?> PingAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ROSHIQ/" + AppUpdater.CurrentVersion);
            using var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            NetworkLog.Record("GET", url, ((int)r.StatusCode).ToString(CultureInfo.InvariantCulture));
            return r.IsSuccessStatusCode ? null : "HTTP " + (int)r.StatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Loc.T("diag_timeout"); }
        catch (Exception ex) { return ex.Message; }
    }
}
