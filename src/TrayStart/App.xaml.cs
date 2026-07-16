using System.Windows;
using System.Windows.Threading;
using TrayStart.Services;
using TrayStart.Views;

namespace TrayStart;

public partial class App : Application
{
    private DispatcherTimer? _startupSweepTimer;
    private DateTime _startupSweepUntil;

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

        // The hook above only sees windows shown from now on. Watched apps that started
        // before TrayStart (typical at sign-in) already have visible windows — sweep them
        // into the tray now, and keep rechecking briefly to catch slow starters.
        SweepWatchedAppWindows();
        StartStartupSweep();

        Updates = new UpdateService();
        _trayIcon = new AppTrayIcon(this);

        Log.Write($"=== TrayStart {Updates.CurrentVersion} started (args: {string.Join(' ', e.Args)}; " +
            $"bootMode={WindowWatcher.InBootMode}; watched: " +
            $"{string.Join(", ", SettingsService.Settings.WatchedApps.Where(w => w.Enabled).Select(w => w.ExeName))}) ===");

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

    /// <summary>Hide already-visible windows of enabled watched apps. Windows the user
    /// deliberately restored are skipped, so repeated sweeps never fight the user.</summary>
    private void SweepWatchedAppWindows()
    {
        foreach (var watched in SettingsService.Settings.WatchedApps.Where(w => w.Enabled))
        {
            Minimizer.MinimizeAllWindowsOf(watched.ExeName,
                suppressIcon: watched.HasOwnTray, respectUserRestore: true);
        }
    }

    /// <summary>Recheck every 10 seconds for the first 2 minutes after launch, and for as
    /// long as boot mode lasts — sign-in apps can take minutes to show their first window
    /// on a busy boot. Stops permanently once both windows have passed.</summary>
    private void StartStartupSweep()
    {
        _startupSweepUntil = DateTime.Now.AddMinutes(2);
        _startupSweepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _startupSweepTimer.Tick += (_, _) =>
        {
            SweepWatchedAppWindows();
            if (DateTime.Now >= _startupSweepUntil && !WindowWatcher.InBootMode)
            {
                Log.Write("startup sweep finished");
                _startupSweepTimer!.Stop();
                _startupSweepTimer = null;
            }
        };
        _startupSweepTimer.Start();
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
