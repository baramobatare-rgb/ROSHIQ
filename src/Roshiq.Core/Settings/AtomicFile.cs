namespace Roshiq.Core.Settings;

/// <summary>
/// Запись файла «всё или ничего»: сначала во временный файл рядом (с принудительным сбросом на диск),
/// затем замена исходного одной операцией. Если запись оборвётся, на месте остаётся прежняя версия,
/// а недописанный «.tmp» игнорируется при чтении.
/// </summary>
public static class AtomicFile
{
    public const string TempSuffix = ".tmp";

    public static void WriteAllText(string path, string text)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + TempSuffix;
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                w.Write(text);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException) { File.Move(tmp, path, overwrite: true); }
            }
            else File.Move(tmp, path);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
