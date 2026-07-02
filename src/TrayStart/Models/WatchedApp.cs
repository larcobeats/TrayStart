using CommunityToolkit.Mvvm.ComponentModel;

namespace TrayStart.Models;

/// <summary>An application the user wants auto-minimized to the tray on launch.</summary>
public partial class WatchedApp : ObservableObject
{
    /// <summary>Executable name including extension, e.g. "spotify.exe". Matched case-insensitively.</summary>
    [ObservableProperty]
    private string _exeName = string.Empty;

    /// <summary>Friendly name shown in the UI (product name if available).</summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>
    /// The app has its own tray icon (auto-detected at add time, user-overridable).
    /// When true, TrayStart hides the window but doesn't add a duplicate tray icon —
    /// the app's native icon is used to bring it back.
    /// </summary>
    [ObservableProperty]
    private bool _hasOwnTray;
}
