using System.Security.Cryptography;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Core.Updates;

/// <summary>
/// Проверка целостности скачанного установщика по SHA256SUMS.txt релиза.
/// Сумма обязательна: нет файла сумм, нет строки для установщика или хэш не совпал — установка отменяется.
/// 0.18.5: GitHub иногда отвечает на маленький файл 504 (особенно через VPN) — пробуем трижды, а если так и не скачался,
/// сверяем с SHA-256, который GitHub сам посчитал для установщика (поле digest в ответе API). Без суммы установка всё так же отменяется.
/// Это проверка целостности (файл не повреждён и совпадает с опубликованным в релизе), а не подлинности издателя —
/// подпись проверяется отдельно.
/// </summary>
public static class InstallerChecksum
{
    public static bool IsGitHubUrl(string url) =>
        url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://release-assets.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Пауза между попытками скачать SHA256SUMS.txt (в тестах подменяется на мгновенную).</summary>
    public static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;
    public static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };

    public static async Task VerifyAsync(ReleaseInfo info, string path, HttpClient http, CancellationToken ct)
    {
        if (info.InstallerName == null || info.ChecksumsUrl == null && info.InstallerDigest == null)
            throw new InvalidOperationException("В релизе нет SHA256SUMS.txt — целостность установщика не проверить. Обновление отменено.");
        if (info.ChecksumsUrl != null && !IsGitHubUrl(info.ChecksumsUrl))
            throw new InvalidOperationException("Неожиданный адрес контрольных сумм: " + info.ChecksumsUrl);

        string? expected = null;
        Exception? lastError = null;
        if (info.ChecksumsUrl != null)
        {
            for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
            {
                if (attempt > 0) await Delay(RetryDelays[attempt - 1], ct);
                try
                {
                    var text = await http.GetStringAsync(info.ChecksumsUrl, ct);
                    NetworkLog.Record("GET", info.ChecksumsUrl, "OK");
                    expected = ExpectedHash(text, info.InstallerName)
                        ?? throw new InvalidOperationException($"В SHA256SUMS.txt нет строки для {info.InstallerName}. Обновление отменено.");
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    lastError = ex;
                    NetworkLog.Record("GET", info.ChecksumsUrl, $"ошибка (попытка {attempt + 1}): " + ex.Message);
                }
            }
        }
        if (expected == null)
        {
            expected = info.InstallerDigest
                ?? throw new InvalidOperationException("Не удалось скачать SHA256SUMS.txt — целостность установщика не проверить. Обновление отменено. " +
                                                       "Скорее всего, GitHub или VPN временно не отвечают — попробуйте через пару минут.", lastError);
            Log.Info("Обновление: SHA256SUMS.txt не скачался — сверяю установщик с SHA-256, который посчитал GitHub");
        }
        string actual;
        await using (var fs = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Контрольная сумма установщика не совпадает — файл повреждён или подменён. Обновление отменено.");
    }

    /// <summary>«sha256:abc…» из поля digest GitHub API → 64 шестнадцатеричных символа (или null).</summary>
    public static string? ParseDigest(string? digest)
    {
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[7..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
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
}
