using System.Security.Cryptography;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Core.Updates;

/// <summary>
/// Проверка целостности скачанного установщика по SHA256SUMS.txt релиза.
/// Сумма обязательна: нет файла сумм, нет строки для установщика или хэш не совпал — установка отменяется.
/// Это проверка целостности (файл не повреждён и совпадает с опубликованным в релизе), а не подлинности издателя —
/// подпись проверяется отдельно.
/// </summary>
public static class InstallerChecksum
{
    public static bool IsGitHubUrl(string url) =>
        url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://release-assets.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);

    public static async Task VerifyAsync(ReleaseInfo info, string path, HttpClient http, CancellationToken ct)
    {
        if (info.ChecksumsUrl == null || info.InstallerName == null)
            throw new InvalidOperationException("В релизе нет SHA256SUMS.txt — целостность установщика не проверить. Обновление отменено.");
        if (!IsGitHubUrl(info.ChecksumsUrl))
            throw new InvalidOperationException("Неожиданный адрес контрольных сумм: " + info.ChecksumsUrl);
        string text;
        try { text = await http.GetStringAsync(info.ChecksumsUrl, ct); }
        catch (HttpRequestException ex)
        {
            NetworkLog.Record("GET", info.ChecksumsUrl, "ошибка: " + ex.Message);
            throw new InvalidOperationException("Не удалось скачать SHA256SUMS.txt — целостность установщика не проверить. Обновление отменено.", ex);
        }
        NetworkLog.Record("GET", info.ChecksumsUrl, "OK");
        var expected = ExpectedHash(text, info.InstallerName)
            ?? throw new InvalidOperationException($"В SHA256SUMS.txt нет строки для {info.InstallerName}. Обновление отменено.");
        string actual;
        await using (var fs = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
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
}
