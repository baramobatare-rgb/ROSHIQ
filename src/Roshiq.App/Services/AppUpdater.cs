using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Localization;
using Roshiq.Core.Updates;

namespace Roshiq.App.Services;

/// <summary>
/// Обновление программы. Проверка релизов — через общий <see cref="UpdateCoordinator"/> (AppHost.Updates):
/// один запрос на всех, кэш, пауза после лимита GitHub, резерв через страницу релизов.
/// Установщик скачивается только по кнопке и запускается после подтверждения пользователя.
/// Перед запуском он обязательно сверяется с SHA256SUMS.txt релиза (без сумм установка отменяется),
/// а если сама программа подписана — проверяется подпись того же издателя.
/// </summary>
public static class AppUpdater
{
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    public static string? NormalizeRepo(string? text) => ReleaseChecker.NormalizeRepo(text);

    public static Version? ParseTag(string? tag) => ReleaseChecker.ParseTag(tag);

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Roshiq/" + CurrentVersion);
        return http;
    }

    /// <summary>Короткая причина для надписи на кнопке (null — если всё хорошо).</summary>
    public static string? ShortStatus(UpdateCheckResult r)
    {
        var L = Loc.Instance;
        return r.Status switch
        {
            UpdateCheckStatus.UpToDate => L.Get("top_update_latest"),
            UpdateCheckStatus.Available => null,
            UpdateCheckStatus.RateLimited => r.RetryAfterUtc is DateTime t
                ? L.Format("top_update_limited", t.ToLocalTime().ToString("HH:mm")) : L.Get("top_update_limited_now"),
            UpdateCheckStatus.Forbidden => L.Get("top_update_forbidden"),
            UpdateCheckStatus.NotFound => L.Get("top_update_notfound"),
            UpdateCheckStatus.NetworkError => L.Get("top_update_offline"),
            _ => L.Get("top_update_failed")
        };
    }

    /// <summary>Подробное объяснение для раздела «Обновления».</summary>
    public static string Describe(UpdateCheckResult r)
    {
        var L = Loc.Instance;
        return r.Status switch
        {
            UpdateCheckStatus.UpToDate => L.Get("upd_app_latest"),
            UpdateCheckStatus.Available => L.Format("upd_app_available", r.Release!.Tag)
                + (r.Release.Source == ReleaseSource.ReleasesPage ? " " + L.Get("upd_via_page") : ""),
            UpdateCheckStatus.RateLimited => r.RetryAfterUtc is DateTime t
                ? L.Format("upd_limited", t.ToLocalTime().ToString("HH:mm")) : L.Get("upd_limited_now"),
            UpdateCheckStatus.Forbidden => L.Get("upd_forbidden"),
            UpdateCheckStatus.NotFound => L.Get("upd_notfound"),
            UpdateCheckStatus.NetworkError => L.Get("upd_offline"),
            _ => L.Format("upd_check_error", r.Message ?? "?")
        };
    }

    /// <summary>Скачать установщик во временную папку. Возвращает путь к файлу.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        if (info.InstallerUrl == null) throw new InvalidOperationException("В релизе нет установщика .exe");
        if (!IsGitHubUrl(info.InstallerUrl))
            throw new InvalidOperationException("Неожиданный адрес установщика: " + info.InstallerUrl);
        var target = Path.Combine(Path.GetTempPath(), "Roshiq-Update-" + info.Version + ".exe");
        using var http = NewClient();
        using var response = await http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? info.InstallerSize;
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(target))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        NetworkLog.Record("GET", info.InstallerUrl, "скачано " + new FileInfo(target).Length + " байт");
        try
        {
            await VerifyChecksumAsync(info, target, http, ct);
            VerifySignature(target);
        }
        catch
        {
            try { File.Delete(target); } catch { }
            throw;
        }
        return target;
    }

    private static bool IsGitHubUrl(string url) => InstallerChecksum.IsGitHubUrl(url);

    private static Task VerifyChecksumAsync(ReleaseInfo info, string path, HttpClient http, CancellationToken ct) =>
        InstallerChecksum.VerifyAsync(info, path, http, ct);

    public static string? ExpectedHash(string sums, string fileName) => InstallerChecksum.ExpectedHash(sums, fileName);

    /// <summary>
    /// Если текущая программа подписана — установщик обязан иметь действительную подпись того же издателя.
    /// Неподписанные сборки (собранные самостоятельно) эту проверку пропускают.
    /// </summary>
    private static void VerifySignature(string installer)
    {
        var self = Environment.ProcessPath;
        var mine = self == null ? null : CodeSignature.Publisher(self);
        if (mine == null || !CodeSignature.IsTrusted(self!)) return;
        if (!CodeSignature.IsTrusted(installer))
            throw new InvalidOperationException("У установщика нет действительной цифровой подписи. Обновление отменено.");
        var theirs = CodeSignature.Publisher(installer);
        if (!string.Equals(mine, theirs, StringComparison.Ordinal))
            throw new InvalidOperationException($"Установщик подписан другим издателем ({theirs ?? "неизвестно"}). Обновление отменено.");
    }

    /// <summary>Программа установлена установщиком — обновление ставится тихо поверх и перезапускает её само.</summary>
    public static bool CanUpdateSilently => InstallerLaunch.IsInstalledCopy(AppContext.BaseDirectory);

    /// <summary>
    /// Запустить проверенный установщик. Для установленной копии — тихо (без мастера, с перезапуском ROSHIQ после установки),
    /// для портативной — обычный мастер.
    /// </summary>
    public static void RunInstaller(string path)
    {
        bool silent = CanUpdateSilently;
        Log.Info("Обновление: запуск установщика " + (silent ? "в тихом режиме (перезапуск после установки)" : "с мастером (портативная копия)"));
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Arguments = InstallerLaunch.Arguments(silent) });
    }

    public static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Не удалось открыть страницу: " + ex.Message); }
    }
}
