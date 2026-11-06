using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Roshiq.Core.Diagnostics;

namespace Roshiq.App.Services;

/// <summary>Свежий релиз ROSHIQ на GitHub.</summary>
public sealed record AppUpdateInfo(Version Version, string Tag, string PageUrl, string? InstallerUrl, string? InstallerName, long InstallerSize,
    string? ChecksumsUrl = null);

/// <summary>
/// Проверка обновлений через GitHub Releases (api.github.com/repos/{owner}/{repo}/releases/latest).
/// Установщик скачивается только по кнопке и запускается после подтверждения пользователя.
/// Перед запуском он проверяется: контрольная сумма из SHA256SUMS.txt релиза и, если сама программа подписана,
/// действительная подпись того же издателя.
/// </summary>
public static class AppUpdater
{
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>«owner/repo» из строки или ссылки github.com/owner/repo.</summary>
    public static string? NormalizeRepo(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().TrimEnd('/');
        const string marker = "github.com/";
        int i = t.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i >= 0) t = t[(i + marker.Length)..];
        var parts = t.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        bool Ok(string s) => s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
        return Ok(parts[0]) && Ok(parts[1]) ? $"{parts[0]}/{parts[1]}" : null;
    }

    /// <summary>«v0.3.2» / «0.3.2-beta» → 0.3.2.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) t = t[..cut];
        if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Roshiq/" + CurrentVersion);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Вернуть сведения о релизе, если он новее текущей версии, иначе null.</summary>
    public static async Task<AppUpdateInfo?> CheckAsync(string repoText, CancellationToken ct)
    {
        var repo = NormalizeRepo(repoText) ?? throw new FormatException("Укажите репозиторий в виде owner/repo");
        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        using var http = NewClient();
        try
        {
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() : null;
            var page = root.TryGetProperty("html_url", out var hu) && hu.ValueKind == JsonValueKind.String ? hu.GetString() : null;
            var version = ParseTag(tag);
            NetworkLog.Record("GET", url, "OK " + (tag ?? "?"));
            if (version == null || version <= CurrentVersion) return null;

            string? installer = null, name = null, sums = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var n = a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() : null;
                    if (n != null && n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        sums = a.TryGetProperty("browser_download_url", out var su) && su.ValueKind == JsonValueKind.String ? su.GetString() : null;
                        continue;
                    }
                    if (n == null || !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!n.Contains("setup", StringComparison.OrdinalIgnoreCase) && installer != null) continue;
                    installer = a.TryGetProperty("browser_download_url", out var du) && du.ValueKind == JsonValueKind.String ? du.GetString() : null;
                    name = n;
                    size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var l) ? l : 0;
                }
            }
            return new AppUpdateInfo(version, tag!, page ?? $"https://github.com/{repo}/releases", installer, name, size, sums);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NetworkLog.Record("GET", url, "ошибка: " + ex.Message);
            throw;
        }
    }

    /// <summary>Скачать установщик во временную папку. Возвращает путь к файлу.</summary>
    public static async Task<string> DownloadAsync(AppUpdateInfo info, IProgress<double>? progress, CancellationToken ct)
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

    private static bool IsGitHubUrl(string url) =>
        url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://release-assets.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сверить SHA-256 скачанного файла с SHA256SUMS.txt релиза (если файл сумм опубликован).</summary>
    private static async Task VerifyChecksumAsync(AppUpdateInfo info, string path, HttpClient http, CancellationToken ct)
    {
        if (info.ChecksumsUrl == null || info.InstallerName == null)
        {
            Log.Warn("В релизе нет SHA256SUMS.txt — контрольная сумма установщика не проверена");
            return;
        }
        if (!IsGitHubUrl(info.ChecksumsUrl)) throw new InvalidOperationException("Неожиданный адрес контрольных сумм: " + info.ChecksumsUrl);
        var text = await http.GetStringAsync(info.ChecksumsUrl, ct);
        NetworkLog.Record("GET", info.ChecksumsUrl, "OK");
        var expected = ExpectedHash(text, info.InstallerName)
            ?? throw new InvalidOperationException($"В SHA256SUMS.txt нет строки для {info.InstallerName}");
        string actual;
        await using (var fs = File.OpenRead(path))
            actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Контрольная сумма установщика не совпадает — файл повреждён или подменён. Обновление отменено.");
    }

    /// <summary>Хэш для файла из строк вида «&lt;sha256&gt;  &lt;имя&gt;» (формат sha256sum).</summary>
    public static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            int space = line.IndexOf(' ');
            if (space <= 0) continue;
            var hash = line[..space];
            var name = line[(space + 1)..].Trim().TrimStart('*');
            if (hash.Length == 64 && name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return hash;
        }
        return null;
    }

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

    public static void RunInstaller(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Не удалось открыть страницу: " + ex.Message); }
    }
}
