using System.Windows;
using TrayStart.Services;
using TrayStart.Views;

namespace TrayStart;

public partial class App : Application
{
    public SettingsService SettingsService { get; private set; } = null!;
    public TrayMinimizer Minimizer { get; private set; } = null!;
    public WindowWatcher Watcher { get; private set; } = null!;
    public UpdateService Updates { get; private set; } = null!;

    private AppTrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _isExiting;

    public AppTrayIcon? TrayIcon => _trayIcon;

    public bool IsExiting => _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // If we ever crash, restore hidden windows so nothing is stranded invisible.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Minimizer?.RestoreAll();

        SettingsService = new SettingsService();
        SettingsService.Load();

        Minimizer = new TrayMinimizer();
        Watcher = new WindowWatcher(SettingsService, Minimizer);
        Watcher.Start();

        Updates = new UpdateService();
        _trayIcon = new AppTrayIcon(this);

        // "--tray" (used by the Run-at-login entry) starts silently in the tray.
        if (!e.Args.Contains("--tray"))
        {
            ShowSettings();
        }

        _ = CheckForUpdatesOnLaunchAsync();
    }

    public void ShowSettings()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(this);
        }
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
    }

    public async Task CheckForUpdatesFromTrayAsync()
    {
        ShowSettings();
        if (_mainWindow?.ViewModel.CheckForUpdatesCommand.CanExecute(null) == true)
        {
            await _mainWindow.ViewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        }
    }

    private async Task CheckForUpdatesOnLaunchAsync()
    {
        if (!Updates.IsSupported) return;
        try
        {
            var update = await Updates.CheckForUpdatesAsync();
            if (update == null) return;

            var result = MessageBox.Show(
                $"TrayStart {update.TargetFullRelease.Version} is available (you have {Updates.CurrentVersion}).\n\n" +
                "Install now? Minimized windows will be restored first.",
                "TrayStart Update", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                Minimizer.RestoreAll();
                await Updates.DownloadAndApplyAsync(update);
            }
        }
        catch
        {
            // Offline or GitHub unreachable — stay quiet on launch; manual check reports errors.
        }
    }

    public void ExitApp()
    {
        _isExiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Watcher?.Dispose();
        Minimizer?.Dispose();   // restores all hidden windows
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
