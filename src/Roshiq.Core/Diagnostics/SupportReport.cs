using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Roshiq.Core.Diagnostics;

/// <summary>
/// «Собрать отчёт»: один zip, который пользователь сам отправляет разработчику (ВКонтакте, GitHub).
/// Внутри — журналы за последние дни, журнал подсказок, кадры распознавания драфта, отчёты о сбоях,
/// настройки без личных данных и сведения о системе. Ничего не отправляется само — файл просто сохраняется.
/// Личное вычищается: Steam ID и имя, ключ OpenDota, токен GSI, путь к профилю Windows.
/// </summary>
public static class SupportReport
{
    public const int LogDays = 7;
    public const int MaxTipFiles = 10;
    public const int MaxCrashFiles = 5;
    public const int MaxVisionFiles = 24;

    /// <summary>Поля настроек, которые в отчёт не попадают (значение заменяется на «скрыто» или null).</summary>
    private static readonly string[] PrivateFields = { "SteamAccountId", "SteamName", "SteamAvatarUrl", "OpenDotaApiKey", "Token", "LeaderboardRank" };

    private static readonly Regex Players = new(@"(players/)\d+", RegexOptions.Compiled);
    private static readonly Regex AccountWords = new(@"((?:account|steam ?id|steamid|аккаунт)[^\d\r\n]{0,12})\d{5,20}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Steam64 = new(@"\b7656119\d{10}\b", RegexOptions.Compiled);

    /// <summary>Собрать отчёт в <paramref name="zipPath"/>. Возвращает число файлов внутри.</summary>
    public static int Create(string dataRoot, string zipPath, string systemInfo, DateTime localNow)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(zipPath)) File.Delete(zipPath);
        int count = 0;
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        void AddText(string name, string text)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(Scrub(text, userProfile));
            count++;
        }
        void AddBinary(string name, string path)
        {
            try { zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal); count++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        static IEnumerable<FileInfo> Newest(string folder, string pattern, int take) =>
            Directory.Exists(folder) ? new DirectoryInfo(folder).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Take(take) : Enumerable.Empty<FileInfo>();

        AddText("system.txt", systemInfo);

        var logs = Path.Combine(dataRoot, "logs");
        foreach (var f in Newest(logs, "*.log", 64).Where(f => f.LastWriteTime >= localNow.AddDays(-LogDays)))
            AddText("logs/" + f.Name, ReadShared(f.FullName));
        foreach (var f in Newest(Path.Combine(logs, "tips"), "*.log", MaxTipFiles))
            AddText("logs/tips/" + f.Name, ReadShared(f.FullName));
        foreach (var f in Newest(Path.Combine(logs, "crashes"), "*.txt", MaxCrashFiles).Where(f => f.Name != "crash-seen.txt"))
            AddText("logs/crashes/" + f.Name, ReadShared(f.FullName));
        foreach (var f in Newest(Path.Combine(logs, "vision"), "*.png", MaxVisionFiles))
            AddBinary("logs/vision/" + f.Name, f.FullName);
        foreach (var name in new[] { "update_history.log" })
        {
            var p = Path.Combine(dataRoot, name);
            if (File.Exists(p)) AddText(name, ReadShared(p));
        }
        var settings = Path.Combine(dataRoot, "settings.json");
        if (File.Exists(settings)) AddText("settings.json", SanitizeSettings(ReadShared(settings)));
        return count;
    }

    /// <summary>Настройки без личных полей. Если файл не разбирается — в отчёт идёт только отметка об этом.</summary>
    public static string SanitizeSettings(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root == null) return "{}";
            Strip(root);
            return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (System.Text.Json.JsonException) { return "{\"_note\":\"settings.json не разбирается — файл повреждён\"}"; }
    }

    private static void Strip(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                if (PrivateFields.Contains(key, StringComparer.OrdinalIgnoreCase))
                    obj[key] = obj[key] is null ? null : "скрыто";
                else if (obj[key] is { } child) Strip(child);
            }
        }
        else if (node is JsonArray arr)
            foreach (var child in arr) if (child != null) Strip(child);
    }

    /// <summary>Убрать из текста Steam ID и путь к профилю Windows (там имя пользователя).</summary>
    public static string Scrub(string text, string? userProfile)
    {
        text = Players.Replace(text, "$1***");
        text = AccountWords.Replace(text, "$1***");
        text = Steam64.Replace(text, "765611***");
        if (!string.IsNullOrEmpty(userProfile))
            text = text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, Encoding.UTF8, true);
            return r.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "<не прочитан: " + ex.Message + ">";
        }
    }
}
