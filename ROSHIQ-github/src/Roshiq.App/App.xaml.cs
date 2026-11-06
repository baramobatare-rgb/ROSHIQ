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
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Необработанное исключение", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Фоновая задача", args.Exception); args.SetObserved(); };

        // перенос данных версии DOTA PULSE — до загрузки настроек
        AppPaths.MigrateLegacyDataRoot();

        try
        {
            Host = new AppHost();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось инициализировать приложение", ex);
            MessageBox.Show("Ошибка запуска: " + ex.Message, "ROSHIQ", MessageBoxButton.OK, MessageBoxImage.Error);
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
        Host.Start();
        Log.Info("ROSHIQ запущен");
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Сбой интерфейса не должен ронять приложение (и тем более влиять на игру)
        Log.Error("Ошибка интерфейса", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Host?.Dispose(); } catch { }
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
