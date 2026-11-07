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

    private static void ShowStartupDialogs(Window owner)
    {
        try
        {
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
