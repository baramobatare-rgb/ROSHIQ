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
