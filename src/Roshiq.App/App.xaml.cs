using System.Windows;
using System.Windows.Threading;
using Roshiq.App.Services;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Settings;
using Roshiq.GSI;

namespace Roshiq.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private StartupState _startup = new(0, false);
    private bool _uiErrorSaved;

    /// <summary>Сохранить отчёт о сбое (никогда не бросает исключений).</summary>
    public static string? SaveCrashReport(Exception ex, string where, string prefix = CrashReport.Prefix)
    {
        try
        {
            var text = CrashReport.Build(ex, AppUpdater.CurrentVersion.ToString(), where, Log.RecentLines, DateTime.Now);
            return CrashReport.Save(AppPaths.CrashReports, text, DateTime.Now, prefix);
        }
        catch { return null; }
    }

    public static AppHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\Roshiq.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("ROSHIQ уже запущен.", "ROSHIQ", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Необработанное исключение", args.ExceptionObject as Exception);
            if (args.ExceptionObject is Exception fatal) SaveCrashReport(fatal, "необработанное исключение (программа закрылась)");
        };
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Фоновая задача", args.Exception); args.SetObserved(); };

        // перенос данных версии DOTA PULSE — до загрузки настроек
        AppPaths.MigrateLegacyDataRoot();

        // два неудачных запуска подряд (или --safe-mode) — безопасный режим
        _startup = CrashGuard.Begin(AppPaths.StartupGuard, e.Args.Any(a => a.Equals("--safe-mode", StringComparison.OrdinalIgnoreCase)));

        try
        {
            Host = new AppHost { SafeMode = _startup.SafeMode };
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось инициализировать приложение", ex);
            var report = SaveCrashReport(ex, "запуск программы");
            MessageBox.Show("Ошибка запуска: " + ex.Message + (report != null ? "\n\nОтчёт сохранён:\n" + report : ""),
                "ROSHIQ", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        Autostart.MigrateLegacy();
        var gsi = Host.Settings.Current.Gsi;
        GsiConfigWriter.MigrateLegacy(SteamLocator.FindDotaRoot(), gsi.Port, gsi.Token);

        ThemeManager.Apply(Host.Settings.Current.Theme);
        var window = new MainWindow();
        MainWindow = window;
        bool minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (minimized && Host.Settings.Current.MinimizeToTray && window.HasTray)
        {
            // запуск с Windows: только значок в трее, окно откроется по клику
        }
        else
        {
            if (minimized) window.WindowState = WindowState.Minimized;
            window.Show();
        }
        _startupFailed = _startup.FailedStarts;
        Host.Start();
        Log.Info(Host.SafeMode ? $"ROSHIQ запущен в безопасном режиме (неудачных запусков подряд: {_startup.FailedStarts})" : "ROSHIQ запущен");

        // мастер первого запуска и «Что нового» — когда окно видно (при запуске в трей — при первом открытии)
        if (window.IsVisible) window.Dispatcher.InvokeAsync(() => ShowStartupDialogs(window), DispatcherPriority.ApplicationIdle);
        else
        {
            DependencyPropertyChangedEventHandler? handler = null;
            handler = (_, _) =>
            {
                if (!window.IsVisible) return;
                window.IsVisibleChanged -= handler;
                window.Dispatcher.InvokeAsync(() => ShowStartupDialogs(window), DispatcherPriority.ApplicationIdle);
            };
            window.IsVisibleChanged += handler;
        }
    }

    private static int _startupFailed;

    private static void ShowStartupDialogs(Window owner)
    {
        try
        {
            // безопасный режим и сбой в прошлый раз — раньше мастера и «Что нового»
            if (Host.SafeMode)
            {
                new Roshiq.App.Views.SafeModeWindow(_startupFailed) { Owner = owner }.ShowDialog();
                return;
            }
            if (CrashReport.Pending(AppPaths.CrashReports) is string crash)
            {
                CrashReport.Acknowledge(crash);
                new Roshiq.App.Views.CrashReportWindow(crash) { Owner = owner }.ShowDialog();
            }

            var settings = Host.Settings;
            var current = Roshiq.App.Services.AppUpdater.CurrentVersion.ToString();
            if (settings.Current.SetupVersion == 0)
            {
                if (settings.IsFreshInstall)
                {
                    new Roshiq.App.Views.FirstRunWindow { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
                    settings.Update(s => { s.SetupVersion = Math.Max(1, s.SetupVersion); s.LastSeenVersion = current; });
                    return;
                }
                settings.Update(s => s.SetupVersion = 1); // обновились со старой версии — всё уже настроено
            }
            if (settings.Current.LastSeenVersion != current)
            {
                var entries = Roshiq.App.Views.WhatsNewWindow.Since(Roshiq.App.Views.WhatsNewWindow.Load(), settings.Current.LastSeenVersion,
                    Roshiq.App.Services.AppUpdater.CurrentVersion);
                settings.Update(s => s.LastSeenVersion = current);
                if (entries.Count > 0) new Roshiq.App.Views.WhatsNewWindow(entries) { Owner = owner }.ShowDialog();
            }
        }
        catch (Exception ex) { Log.Error("Окна при запуске", ex); }
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Сбой интерфейса не должен ронять приложение (и тем более влиять на игру)
        Log.Error("Ошибка интерфейса", e.Exception);
        if (!_uiErrorSaved)
        {
            _uiErrorSaved = true; // один отчёт за запуск, чтобы не засорять папку
            SaveCrashReport(e.Exception, "интерфейс (программа продолжила работу)", CrashReport.ErrorPrefix);
        }
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Host?.Dispose(); } catch { }
        if (e.ApplicationExitCode == 0 && Host != null) CrashGuard.MarkStable(AppPaths.StartupGuard);
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
