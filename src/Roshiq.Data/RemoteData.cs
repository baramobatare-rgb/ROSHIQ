using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Data;

/// <summary>Результат проверки обновления данных.</summary>
public sealed record RemoteDataResult(bool Updated, string? Version, string Message);

/// <summary>
/// Обновление файлов данных (герои, предметы, таймеры, правила…) с GitHub без выпуска новой версии программы.
/// Файлы берутся из src/Roshiq.Data/Resources репозитория, проверяются загрузкой во временной папке и только потом
/// заменяют прежнее обновление. Ваши собственные правки (папка data) всегда важнее.
/// </summary>
public static class RemoteData
{
    public static string BaseUrl(string repo) => $"https://raw.githubusercontent.com/{repo}/main/src/Roshiq.Data/Resources/";

    /// <summary>Разбор data_version.txt (строки ключ=значение).</summary>
    public static Dictionary<string, string> ParseVersion(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int i = line.IndexOf('=');
            if (i > 0) d[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return d;
    }

    /// <summary>Версии данных вида 2026.11.07 или 2026.11.04b сравниваются как строки одного формата.</summary>
    public static bool IsNewer(string? remote, string? current)
    {
        var cur = (current ?? "").Trim();
        if (cur == "?") cur = "";
        return !string.IsNullOrWhiteSpace(remote) && string.CompareOrdinal(remote.Trim(), cur) > 0;
    }

    /// <summary>Версия программы, нужная данным (minApp), не выше текущей.</summary>
    public static bool AppIsEnough(string? minApp, Version app) =>
        string.IsNullOrWhiteSpace(minApp) || !Version.TryParse(minApp, out var need) || app >= need;

    /// <summary>Проверка загруженных данных: столько же героев и предметов, сколько во встроенных (с запасом), и мало ошибок.</summary>
    public static string? Validate(GameDatabase candidate, GameDatabase embedded)
    {
        if (candidate.Heroes.Count < embedded.Heroes.Count * 0.9) return $"героев {candidate.Heroes.Count} — слишком мало";
        if (candidate.Items.Count < embedded.Items.Count * 0.8) return $"предметов {candidate.Items.Count} — слишком мало";
        if (candidate.Warnings.Count > embedded.Warnings.Count + 20) return $"ошибок в данных: {candidate.Warnings.Count}";
        return null;
    }

    /// <summary>
    /// Проверить и, если есть, установить новые данные в remoteDir. currentVersion — версия данных, загруженных сейчас.
    /// </summary>
    public static async Task<RemoteDataResult> UpdateAsync(HttpClient http, string repo, string currentVersion, Version appVersion,
        string remoteDir, CancellationToken ct)
    {
        var baseUrl = BaseUrl(repo);
        var versionText = await GetAsync(http, baseUrl + "data_version.txt", ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("на GitHub нет data_version.txt");
        var info = ParseVersion(versionText);
        info.TryGetValue("dataVersion", out var remoteVersion);
        if (!IsNewer(remoteVersion, currentVersion)) return new RemoteDataResult(false, currentVersion, "данные актуальны");
        info.TryGetValue("minApp", out var minApp);
        if (!AppIsEnough(minApp, appVersion))
            return new RemoteDataResult(false, currentVersion, $"данные {remoteVersion} требуют ROSHIQ {minApp} или новее");

        var staging = remoteDir + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllText(Path.Combine(staging, "data_version.txt"), versionText);
            foreach (var file in GameDatabase.ResourceFiles.Where(f => f != "data_version.txt"))
            {
                var text = await GetAsync(http, baseUrl + file, ct).ConfigureAwait(false);
                if (text != null) File.WriteAllText(Path.Combine(staging, file), text);
            }
            var candidate = GameDatabase.Load(null, staging);
            if (Validate(candidate, GameDatabase.Load()) is string problem)
                return new RemoteDataResult(false, currentVersion, "обновление данных отклонено: " + problem);

            if (Directory.Exists(remoteDir)) Directory.Delete(remoteDir, true);
            Directory.Move(staging, remoteDir);
            Log.Info($"Данные обновлены с GitHub: {currentVersion} → {remoteVersion}");
            return new RemoteDataResult(true, remoteVersion, $"данные обновлены до {remoteVersion}");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    /// <summary>Версия встроенных данных (из ресурса data_version.txt).</summary>
    public static string EmbeddedVersion()
    {
        using var stream = typeof(RemoteData).Assembly.GetManifestResourceStream("Roshiq.Data.data_version.txt");
        if (stream == null) return "";
        using var reader = new StreamReader(stream);
        return ParseVersion(reader.ReadToEnd()).TryGetValue("dataVersion", out var v) ? v : "";
    }

    /// <summary>
    /// Папка обновления, если она новее встроенных данных; иначе null (после установки новой версии программы
    /// её встроенные данные свежее старого скачанного обновления — тогда скачанное не используется).
    /// </summary>
    public static string? UsableRemoteDir(string remoteDir)
    {
        try
        {
            var file = Path.Combine(remoteDir, "data_version.txt");
            if (!File.Exists(file)) return null;
            var remote = ParseVersion(File.ReadAllText(file)).TryGetValue("dataVersion", out var v) ? v : null;
            return IsNewer(remote, EmbeddedVersion()) ? remoteDir : null;
        }
        catch { return null; }
    }

    /// <summary>Сбросить обновление данных — вернуться к встроенным.</summary>
    public static void Reset(string remoteDir)
    {
        try { if (Directory.Exists(remoteDir)) Directory.Delete(remoteDir, true); }
        catch (Exception ex) { Log.Warn("Не удалось удалить обновление данных: " + ex.Message); }
    }

    private static async Task<string?> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        NetworkLog.Record("GET", url, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Что в данных ROSHIQ устарело относительно OpenDota: новые герои, исчезнувшие предметы, отставание по патчу.</summary>
public sealed record DataAudit(string? LatestPatch, string Baseline, bool PatchBehind,
    IReadOnlyList<string> NewHeroes, IReadOnlyList<string> RemovedItems, DateTime CheckedUtc)
{
    public bool HasIssues => PatchBehind || NewHeroes.Count > 0 || RemovedItems.Count > 0;

    /// <summary>
    /// openDotaHeroes — id → отображаемое имя; openDotaItems — ключи item_xxx, существующие сейчас.
    /// Если списки OpenDota пустые (не загрузились), соответствующая проверка пропускается.
    /// </summary>
    public static DataAudit Build(GameDatabase db, string? latestPatch, IReadOnlyDictionary<int, string> openDotaHeroes,
        IReadOnlyCollection<string> openDotaItems)
    {
        var newHeroes = openDotaHeroes.Where(h => db.FindHero(h.Key) == null).Select(h => h.Value).OrderBy(n => n).ToList();
        var items = new HashSet<string>(openDotaItems, StringComparer.OrdinalIgnoreCase);
        var removed = items.Count == 0 ? new List<string>()
            : db.Items.Where(i => !items.Contains(i.Key)).Select(i => i.Name).OrderBy(n => n).ToList();
        bool behind = PatchVersion.IsNewer(latestPatch, db.PatchBaseline);
        return new DataAudit(latestPatch, db.PatchBaseline, behind, newHeroes, removed, DateTime.UtcNow);
    }
}
