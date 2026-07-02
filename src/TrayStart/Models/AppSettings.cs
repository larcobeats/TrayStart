namespace TrayStart.Models;

public class AppSettings
{
    public List<WatchedApp> WatchedApps { get; set; } = new();

    /// <summary>
    /// Windows shown within this many seconds of a watched process starting are
    /// auto-minimized. Windows the user opens later (from the app's own UI) are left alone.
    /// </summary>
    public int GraceSeconds { get; set; } = 15;

    /// <summary>Show a one-time balloon tip the first time the settings window is closed to tray.</summary>
    public bool ShownCloseToTrayTip { get; set; }
}
