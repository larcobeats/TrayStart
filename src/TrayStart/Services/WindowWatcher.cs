using System.Diagnostics;
using TrayStart.Interop;

namespace TrayStart.Services;

/// <summary>
/// Event-driven watcher for new top-level windows. Uses an out-of-context WinEvent hook
/// (EVENT_OBJECT_SHOW), so it consumes zero CPU while idle — Windows calls us only when
/// a window actually appears. Must be started on a thread with a message loop (the WPF UI thread).
/// </summary>
public class WindowWatcher : IDisposable
{
    /// <summary>
    /// For the first minutes after Windows boots, every watched-app window is autostart
    /// fallout, not user action — hide unconditionally instead of applying the process-age
    /// grace check. Apps like TikFinity relaunch through an updater and can take minutes
    /// to show their first window on a busy sign-in.
    /// </summary>
    public static bool InBootMode => Environment.TickCount64 < BootModeMinutes * 60_000L;

    public const int BootModeMinutes = 5;

    private readonly SettingsService _settings;
    private readonly TrayMinimizer _minimizer;

    // Keep a strong reference to the delegate so the GC never collects the hook callback.
    private NativeMethods.WinEventDelegate? _callback;
    private IntPtr _hook;

    // Fallback launch-time tracking for processes whose StartTime we can't read (e.g. elevated).
    private readonly Dictionary<uint, DateTime> _firstSeen = new();
    private DateTime _lastPrune = DateTime.Now;

    public WindowWatcher(SettingsService settings, TrayMinimizer minimizer)
    {
        _settings = settings;
        _minimizer = minimizer;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _callback = OnWinEvent;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType != NativeMethods.EVENT_OBJECT_SHOW) return;
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF) return;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            HandleWindowShown(hwnd);
        }
        catch
        {
            // Never let an exception escape the hook callback.
        }
    }

    private void HandleWindowShown(IntPtr hwnd)
    {
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd) return;

        // A window we already trayed is showing again: the app force-showed itself during
        // startup (re-hide it) or the user brought it back intentionally (release it).
        if (_minimizer.IsTrayed(hwnd))
        {
            _minimizer.HandleReShown(hwnd, _settings.Settings.GraceSeconds);
            return;
        }

        // Only real, unowned, titled top-level windows — filters tooltips, popups, splash fragments.
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return;
        if (!NativeMethods.IsWindowVisible(hwnd)) return;
        if (NativeMethods.GetWindowTextLength(hwnd) == 0) return;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return;

        if (_minimizer.WasRestoredByUser(hwnd)) return;

        Process process;
        try
        {
            process = Process.GetProcessById((int)pid);
        }
        catch
        {
            return; // Process already exited.
        }

        string exeName = process.ProcessName + ".exe";
        var watched = _settings.Settings.WatchedApps.FirstOrDefault(w =>
            w.Enabled && string.Equals(w.ExeName, exeName, StringComparison.OrdinalIgnoreCase));
        if (watched == null) return;

        // Boot mode bypasses the grace check — unless the user is actively clicking or
        // typing right now, in which case this window may be one they just opened; defer
        // to the normal rules (the startup sweep still catches true autostart windows).
        bool bootModeHide = InBootMode && NativeMethods.SecondsSinceLastInput() > 3;
        if (!bootModeHide && !IsWithinGracePeriod(process, pid)) return;

        _minimizer.MinimizeToTray(hwnd, process, suppressIcon: watched.HasOwnTray);
    }

    /// <summary>
    /// Only hide windows shown shortly after the process started. Windows the user
    /// opens later (e.g. reopening from the app's own tray icon) are left alone.
    /// </summary>
    private bool IsWithinGracePeriod(Process process, uint pid)
    {
        int grace = Math.Max(1, _settings.Settings.GraceSeconds);
        try
        {
            return (DateTime.Now - process.StartTime).TotalSeconds <= grace;
        }
        catch
        {
            // StartTime is unreadable (elevated / other-user process): fall back to
            // when we first saw a window from this PID.
            PruneFirstSeen();
            if (!_firstSeen.TryGetValue(pid, out var seen))
            {
                _firstSeen[pid] = DateTime.Now;
                return true;
            }
            return (DateTime.Now - seen).TotalSeconds <= grace;
        }
    }

    private void PruneFirstSeen()
    {
        if ((DateTime.Now - _lastPrune).TotalMinutes < 10) return;
        _lastPrune = DateTime.Now;
        var cutoff = DateTime.Now.AddMinutes(-10);
        foreach (var stale in _firstSeen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
        {
            _firstSeen.Remove(stale);
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        _callback = null;
        GC.SuppressFinalize(this);
    }
}
