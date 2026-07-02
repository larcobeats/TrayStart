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
}
