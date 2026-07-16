using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayStart.Models;
using TrayStart.Services;
using TrayStart.Views;

namespace TrayStart.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly App _app;
    private readonly SettingsService _settings;

    public ObservableCollection<WatchedApp> WatchedApps { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    [NotifyCanExecuteChangedFor(nameof(MinimizeNowCommand))]
    private WatchedApp? _selectedApp;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _updateInProgress;

    public string VersionText => $"v{_app.Updates.CurrentVersion}";

    public MainViewModel(App app)
    {
        _app = app;
        _settings = app.SettingsService;

        WatchedApps = new ObservableCollection<WatchedApp>(_settings.Settings.WatchedApps);
        foreach (var wa in WatchedApps)
        {
            wa.PropertyChanged += OnWatchedAppChanged;
        }

        _startWithWindows = StartupService.IsEnabled();

        if (!_app.Updates.IsSupported)
        {
            UpdateStatus = "Dev build — updates work in the installed version.";
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        try
        {
            StartupService.SetEnabled(value);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't update the startup setting:\n{ex.Message}", "TrayStart",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnWatchedAppChanged(object? sender, PropertyChangedEventArgs e) => SaveWatchedApps();

    private void SaveWatchedApps()
    {
        _settings.Settings.WatchedApps = WatchedApps.ToList();
        _settings.Save();
    }

    [RelayCommand]
    private void AddFromFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a program to auto-minimize",
            Filter = "Programs (*.exe)|*.exe",
        };
        if (dialog.ShowDialog() != true) return;

        string exeName = Path.GetFileName(dialog.FileName);
        string display = GetDisplayName(dialog.FileName) ?? Path.GetFileNameWithoutExtension(dialog.FileName);
        AddWatchedApp(exeName, display);
    }

    [RelayCommand]
    private void AddFromRunning()
    {
        var picker = new RunningAppsWindow { Owner = Application.Current.MainWindow };
        if (picker.ShowDialog() == true && picker.SelectedEntry != null)
        {
            AddWatchedApp(picker.SelectedEntry.ExeName, picker.SelectedEntry.DisplayName);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (SelectedApp == null) return;
        SelectedApp.PropertyChanged -= OnWatchedAppChanged;
        WatchedApps.Remove(SelectedApp);
        SelectedApp = null;
        SaveWatchedApps();
    }

    private bool CanRemove() => SelectedApp != null;

    [RelayCommand(CanExecute = nameof(CanMinimizeNow))]
    private void MinimizeNow()
    {
        if (SelectedApp == null) return;
        Log.Write($"manual minimize requested for {SelectedApp.ExeName}");
        int count = _app.Minimizer.MinimizeAllWindowsOf(SelectedApp.ExeName, suppressIcon: SelectedApp.HasOwnTray);
        if (count == 0)
        {
            MessageBox.Show(
                $"No open windows found for {SelectedApp.DisplayName} ({SelectedApp.ExeName}).",
                "TrayStart", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private bool CanMinimizeNow() => SelectedApp != null;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        if (!_app.Updates.IsSupported)
        {
            UpdateStatus = "Updates are only available in the installed version.";
            return;
        }

        UpdateInProgress = true;
        UpdateStatus = "Checking for updates…";
        try
        {
            var update = await _app.Updates.CheckForUpdatesAsync();
            if (update == null)
            {
                UpdateStatus = $"You're up to date ({VersionText}).";
                return;
            }

            UpdateStatus = $"Version {update.TargetFullRelease.Version} is available.";
            var result = MessageBox.Show(
                $"TrayStart {update.TargetFullRelease.Version} is available (you have {_app.Updates.CurrentVersion}).\n\n" +
                "Install now? Minimized windows will be restored first.",
                "TrayStart Update", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                UpdateStatus = "Downloading update…";
                _app.Minimizer.RestoreAll();
                await _app.Updates.DownloadAndApplyAsync(update);
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update check failed: {ex.Message}";
        }
        finally
        {
            UpdateInProgress = false;
        }
    }

    private bool CanCheckForUpdates() => !UpdateInProgress;

    private void AddWatchedApp(string exeName, string displayName)
    {
        if (WatchedApps.Any(w => string.Equals(w.ExeName, exeName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var app = new WatchedApp
        {
            ExeName = exeName,
            DisplayName = displayName,
            Enabled = true,
            // Apps with a native tray icon don't get a duplicate TrayStart icon (user-overridable).
            HasOwnTray = TrayIconRegistry.ExeHasOwnTrayIcon(exeName),
        };
        app.PropertyChanged += OnWatchedAppChanged;
        WatchedApps.Add(app);
        SaveWatchedApps();
    }

    private static string? GetDisplayName(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            return string.IsNullOrWhiteSpace(info.ProductName) ? info.FileDescription : info.ProductName;
        }
        catch
        {
            return null;
        }
    }
}
