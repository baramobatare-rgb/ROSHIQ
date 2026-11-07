using System.Text.RegularExpressions;
using Roshiq.Core.Diagnostics;
using Microsoft.Win32;

namespace Roshiq.GSI;

/// <summary>
/// Создание конфигурационного файла GSI — официальный механизм Valve:
/// файл gamestate_integration_*.cfg кладётся в game\dota\cfg\gamestate_integration.
/// Игровые файлы при этом не изменяются. Для работы может потребоваться параметр запуска -gamestateintegration.
/// </summary>
public static class GsiConfigWriter
{
    public const string FileName = "gamestate_integration_roshiq.cfg";

    /// <summary>Файл, который создавали версии до переименования (DOTA PULSE).</summary>
    public const string LegacyFileName = "gamestate_integration_dotapulse.cfg";

    public static string BuildContent(int port, string token) =>
$@"""Roshiq""
{{
    ""uri""           ""http://127.0.0.1:{port}/""
    ""timeout""       ""5.0""
    ""buffer""        ""0.1""
    ""throttle""      ""0.5""
    ""heartbeat""     ""30.0""
    ""data""
    {{
        ""provider""      ""1""
        ""map""           ""1""
        ""player""        ""1""
        ""hero""          ""1""
        ""abilities""     ""0""
        ""items""         ""1""
    }}
    ""auth""
    {{
        ""token""         ""{token}""
    }}
}}
";

    public static string TargetPath(string dotaRoot) =>
        Path.Combine(dotaRoot, "game", "dota", "cfg", "gamestate_integration", FileName);

    /// <summary>Записать cfg. Возвращает путь. Вызывается только по явному действию пользователя.</summary>
    public static string Write(string dotaRoot, int port, string token)
    {
        var path = TargetPath(dotaRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, BuildContent(port, token));
        Log.Info("GSI: записан конфиг " + path);
        DeleteLegacy(dotaRoot);
        return path;
    }

    /// <summary>
    /// Старый cfg шлёт данные на тот же порт — вместе с новым Dota отправляла бы каждое событие дважды.
    /// Если старый файл есть, а нового нет, он заменяется новым с теми же портом и токеном.
    /// </summary>
    public static void MigrateLegacy(string? dotaRoot, int port, string token)
    {
        if (string.IsNullOrWhiteSpace(dotaRoot)) return;
        try
        {
            var legacy = Path.Combine(Path.GetDirectoryName(TargetPath(dotaRoot))!, LegacyFileName);
            if (!File.Exists(legacy)) return;
            if (!Exists(dotaRoot)) Write(dotaRoot, port, token);
            else DeleteLegacy(dotaRoot);
        }
        catch (Exception ex)
        {
            Log.Warn("GSI: не удалось заменить старый конфиг: " + ex.Message);
        }
    }

    private static void DeleteLegacy(string dotaRoot)
    {
        var legacy = Path.Combine(Path.GetDirectoryName(TargetPath(dotaRoot))!, LegacyFileName);
        try
        {
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
                Log.Info("GSI: удалён старый конфиг " + legacy);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("GSI: не удалось удалить старый конфиг: " + ex.Message);
        }
    }

    public static bool Exists(string dotaRoot) => File.Exists(TargetPath(dotaRoot));
}

/// <summary>Поиск установленной Dota 2 через реестр Steam и libraryfolders.vdf (только чтение).</summary>
public static class SteamLocator
{
    public static string? FindSteamRoot()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path)) return path.Replace('/', Path.DirectorySeparatorChar);
        }
        catch (Exception ex) { Log.Warn("Steam: реестр недоступен: " + ex.Message); }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    public static IReadOnlyList<string> ParseLibraryFolders(string vdfText)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(vdfText, "\"path\"\\s+\"([^\"]+)\""))
            list.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        return list;
    }

    public static string? FindDotaRoot()
    {
        var steam = FindSteamRoot();
        if (steam == null) return null;
        var libraries = new List<string> { steam };
        try
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf)) libraries.AddRange(ParseLibraryFolders(File.ReadAllText(vdf)));
        }
        catch (Exception ex) { Log.Warn("Steam: не удалось прочитать libraryfolders.vdf: " + ex.Message); }

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(lib, "steamapps", "common", "dota 2 beta");
            if (Directory.Exists(Path.Combine(candidate, "game", "dota"))) return candidate;
        }
        return null;
    }
}

/// <summary>
/// Параметры запуска Dota 2 из Steam (userdata\&lt;id&gt;\config\localconfig.vdf, только чтение).
/// Нужны, чтобы подсказать про -gamestateintegration: без него новые версии Dota 2 не отправляют данные.
/// </summary>
public static class DotaLaunchOptions
{
    public const string AppId = "570";
    public const string GsiFlag = "-gamestateintegration";

    /// <summary>Параметры запуска приложения appId из текста localconfig.vdf; null — если запись не найдена.</summary>
    public static string? Parse(string vdfText, string appId = AppId)
    {
        // Минимальный разбор KeyValues: строки в кавычках и фигурные скобки.
        var path = new List<string>();
        string? pendingKey = null;
        int i = 0;
        while (i < vdfText.Length)
        {
            char c = vdfText[i];
            if (c == '"')
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < vdfText.Length && vdfText[i] != '"')
                {
                    if (vdfText[i] == '\\' && i + 1 < vdfText.Length) { sb.Append(vdfText[i + 1]); i += 2; continue; }
                    sb.Append(vdfText[i++]);
                }
                i++;
                var token = sb.ToString();
                if (pendingKey == null) pendingKey = token;
                else
                {
                    if (pendingKey.Equals("LaunchOptions", StringComparison.OrdinalIgnoreCase) && path.Count >= 2
                        && path[^1] == appId && path[^2].Equals("apps", StringComparison.OrdinalIgnoreCase))
                        return token;
                    pendingKey = null;
                }
            }
            else if (c == '{') { path.Add(pendingKey ?? ""); pendingKey = null; i++; }
            else if (c == '}') { if (path.Count > 0) path.RemoveAt(path.Count - 1); pendingKey = null; i++; }
            else if (c == '/' && i + 1 < vdfText.Length && vdfText[i + 1] == '/') { while (i < vdfText.Length && vdfText[i] != '\n') i++; }
            else i++;
        }
        return null;
    }

    public static bool HasGsiFlag(string? options) =>
        options != null && options.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(o => o.Equals(GsiFlag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Есть ли -gamestateintegration у Dota 2 хотя бы в одном аккаунте Steam на этом ПК.
    /// true/false — найдено/не найдено; null — Steam или настройки Dota не найдены.
    /// </summary>
    public static bool? CheckInstalled()
    {
        try
        {
            var steam = SteamLocator.FindSteamRoot();
            var userdata = steam == null ? null : Path.Combine(steam, "userdata");
            if (userdata == null || !Directory.Exists(userdata)) return null;
            bool any = false;
            foreach (var dir in Directory.GetDirectories(userdata))
            {
                var file = Path.Combine(dir, "config", "localconfig.vdf");
                if (!File.Exists(file)) continue;
                var text = File.ReadAllText(file);
                if (!text.Contains("\"" + AppId + "\"")) continue;
                any = true;
                if (HasGsiFlag(Parse(text))) return true;
            }
            return any ? false : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Steam: не удалось прочитать параметры запуска: " + ex.Message);
            return null;
        }
    }
}
