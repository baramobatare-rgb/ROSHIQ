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
    public static string DataOverrides => Path.Combine(DataRoot, "data");
    public static string Matches => Path.Combine(DataRoot, "matches");
}
