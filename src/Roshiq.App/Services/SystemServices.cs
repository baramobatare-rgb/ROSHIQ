using System.Diagnostics;
using Roshiq.Core.Diagnostics;
using Microsoft.Win32;

namespace Roshiq.App.Services;

/// <summary>Диагностика нагрузки самого Roshiq: CPU (% от всех ядер) и память.</summary>
public sealed class ProcessMonitor
{
    private readonly Process _self = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastSample;

    public ProcessMonitor()
    {
        _lastCpu = _self.TotalProcessorTime;
        _lastSample = DateTime.UtcNow;
    }

    public double CpuPercent { get; private set; }
    public double WorkingSetMb { get; private set; }
    public double ManagedHeapMb { get; private set; }
    public int ThreadCount { get; private set; }

    public void Sample()
    {
        try
        {
            _self.Refresh();
            var now = DateTime.UtcNow;
            var cpu = _self.TotalProcessorTime;
            var elapsed = (now - _lastSample).TotalMilliseconds;
            if (elapsed > 0)
                CpuPercent = (cpu - _lastCpu).TotalMilliseconds / (elapsed * Environment.ProcessorCount) * 100.0;
            _lastCpu = cpu;
            _lastSample = now;
            WorkingSetMb = _self.WorkingSet64 / 1024.0 / 1024.0;
            ManagedHeapMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
            ThreadCount = _self.Threads.Count;
        }
        catch (Exception ex)
        {
            Log.Warn("Диагностика недоступна: " + ex.Message);
        }
    }
}

/// <summary>Автозапуск через HKCU\...\Run (без прав администратора).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Roshiq";
    private const string LegacyValueName = "DotaPulse";

    /// <summary>Автозапуск, включённый до переименования, переносится на новое имя и новый .exe.</summary>
    public static void MigrateLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) == null) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            Set(true);
        }
        catch (Exception ex)
        {
            Log.Warn("Автозапуск: не удалось перенести старую запись: " + ex.Message);
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (exe == null) return false;
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Автозапуск: ошибка", ex);
            return false;
        }
    }
}
