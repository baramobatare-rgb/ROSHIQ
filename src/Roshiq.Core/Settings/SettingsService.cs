using System.Text.Json;
using System.Text.Json.Serialization;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Core.Settings;

/// <summary>Загрузка/сохранение/импорт/экспорт настроек в JSON.</summary>
public sealed class SettingsService
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _path;
    private readonly object _lock = new();

    public SettingsService(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataRoot, "settings.json");
        Current = Load();
    }

    public string FilePath => _path;
    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    /// <summary>Файла настроек не было — это первый запуск ROSHIQ на этом компьютере.</summary>
    public bool IsFreshInstall { get; private set; }

    public AppSettings Load()
    {
        IsFreshInstall = !File.Exists(_path);
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (s != null) return s.Normalize();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать настройки, используются значения по умолчанию: {ex.Message}");
            TryBackupBroken();
        }
        return new AppSettings().Normalize();
    }

    public void Save()
    {
        lock (_lock)
        {
            Current.Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        Changed?.Invoke(this, Current);
    }

    /// <summary>Изменить настройки и сохранить.</summary>
    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Save();
    }

    public void Export(string targetPath)
    {
        File.WriteAllText(targetPath, JsonSerializer.Serialize(Current, JsonOptions));
    }

    /// <summary>Импорт. Неверный файл не портит текущие настройки.</summary>
    public bool Import(string sourcePath, out string? error)
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(sourcePath), JsonOptions);
            if (s == null) { error = "empty"; return false; }
            Current = s.Normalize();
            Save();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Резервная копия текущих настроек в папку backups (хранятся последние 10). Возвращает путь или null.</summary>
    public string? Backup(string reason)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var dir = Path.Combine(Path.GetDirectoryName(_path)!, "backups");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"settings-{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.json");
            File.Copy(_path, target, overwrite: true);
            foreach (var old in Directory.GetFiles(dir, "settings-*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(10))
            {
                try { File.Delete(old); } catch { }
            }
            return target;
        }
        catch (Exception ex)
        {
            Log.Warn("Резервная копия настроек: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Сбросить настройки к значениям по умолчанию (предварительно сохранив копию).
    /// Язык, привязка Steam и отметка о пройденном мастере сохраняются.
    /// </summary>
    public string? ResetToDefaults()
    {
        var backup = Backup("reset");
        var keep = Current;
        var fresh = new AppSettings
        {
            Language = keep.Language,
            SetupVersion = keep.SetupVersion,
            LastSeenVersion = keep.LastSeenVersion
        };
        fresh.Data.SteamAccountId = keep.Data.SteamAccountId;
        fresh.Data.SteamLinkedVia = keep.Data.SteamLinkedVia;
        fresh.Data.SteamName = keep.Data.SteamName;
        fresh.Data.SteamAvatarUrl = keep.Data.SteamAvatarUrl;
        fresh.Data.AllowOnlineStats = keep.Data.AllowOnlineStats;
        Current = fresh.Normalize();
        Save();
        Log.Info("Настройки сброшены к значениям по умолчанию" + (backup != null ? ", копия: " + backup : ""));
        return backup;
    }

    private void TryBackupBroken()
    {
        try { File.Copy(_path, _path + ".broken", overwrite: true); } catch { /* ignore */ }
    }
}

public static class AppPaths
{
    /// <summary>Папка данных до переименования проекта (DOTA PULSE).</summary>
    public static string LegacyDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotaPulse");

    /// <summary>
    /// Однократный перенос настроек, шаблонов и логов из %AppData%\DotaPulse в %AppData%\Roshiq.
    /// Вызывается при запуске до загрузки настроек. Старая папка переименовывается целиком;
    /// если это невозможно (например, открыт файл), содержимое копируется, а старая папка остаётся.
    /// </summary>
    public static void MigrateLegacyDataRoot()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ROSHIQ_HOME"))) return;
            var oldRoot = LegacyDataRoot;
            var newRoot = DataRoot;
            if (!Directory.Exists(oldRoot) || Directory.Exists(newRoot)) return;
            try
            {
                Directory.Move(oldRoot, newRoot);
            }
            catch (IOException)
            {
                CopyTree(oldRoot, newRoot);
            }
            catch (UnauthorizedAccessException)
            {
                CopyTree(oldRoot, newRoot);
            }
        }
        catch
        {
            // перенос — удобство, не обязательное условие запуска
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            try { File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: false); }
            catch { }
        }
    }

    /// <summary>%AppData%\Roshiq (можно переопределить переменной ROSHIQ_HOME).</summary>
    public static string DataRoot
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ROSHIQ_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roshiq");
        }
    }

    public static string Templates => Path.Combine(DataRoot, "templates");
    public static string Cache => Path.Combine(DataRoot, "cache");
    /// <summary>Файлы данных, скачанные с GitHub (обновление данных без новой версии программы).</summary>
    public static string DataRemote => Path.Combine(DataRoot, "data-remote");
    public static string Logs => Path.Combine(DataRoot, "logs");
    /// <summary>Отчёты о сбоях (crash-*.txt).</summary>
    public static string CrashReports => Path.Combine(DataRoot, "logs", "crashes");
    /// <summary>Счётчик неудачных запусков подряд (для безопасного режима).</summary>
    public static string StartupGuard => Path.Combine(DataRoot, "startup-guard.txt");
    /// <summary>Резервные копии настроек.</summary>
    public static string Backups => Path.Combine(DataRoot, "backups");
    public static string DataOverrides => Path.Combine(DataRoot, "data");
    public static string Matches => Path.Combine(DataRoot, "matches");
}
