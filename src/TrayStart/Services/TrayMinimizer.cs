using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using TrayStart.Interop;

namespace TrayStart.Services;

/// <summary>
/// Hides windows and represents each one as a tray icon (using the window's own icon).
/// Left-click restores the window; right-click offers Restore / Close.
/// All windows are restored when TrayStart exits so nothing gets stranded invisible.
/// </summary>
public class TrayMinimizer : IDisposable
{
    private sealed class TrayedWindow
    {
        public required IntPtr Hwnd { get; init; }
        // Null when the app has its own tray icon and ours would be a duplicate.
        public NotifyIcon? Icon { get; init; }
        public Process? Process { get; init; }
        public DateTime TrayedAt { get; } = DateTime.Now;
        public int ReHideCount { get; set; }
    }

    // Give up re-hiding a window that keeps force-showing itself after this many attempts.
    private const int MaxReHides = 8;

    private readonly Dictionary<IntPtr, TrayedWindow> _trayed = new();

    // Windows the user explicitly restored — never auto-hide these again.
    private readonly HashSet<IntPtr> _restoredByUser = new();

    public bool IsTrayed(IntPtr hwnd) => _trayed.ContainsKey(hwnd);

    public bool WasRestoredByUser(IntPtr hwnd) => _restoredByUser.Contains(hwnd);

    /// <param name="suppressIcon">The app has its own tray icon; hide the window but don't add a duplicate icon.</param>
    public void MinimizeToTray(IntPtr hwnd, Process process, bool suppressIcon = false)
    {
        if (IsTrayed(hwnd)) return;
        _restoredByUser.Remove(hwnd); // an explicit tray request overrides an earlier restore

        string title = NativeMethods.GetWindowTitle(hwnd);
        Icon? icon = suppressIcon ? null : GetWindowIcon(hwnd, process);

        HideWindow(hwnd);
        if (NativeMethods.IsWindowVisible(hwnd))
        {
            // Hide was blocked (e.g. elevated window and we're not) — don't orphan a tray icon.
            icon?.Dispose();
            return;
        }

        NotifyIcon? notifyIcon = null;
        if (icon != null)
        {
            notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Text = Truncate(string.IsNullOrEmpty(title) ? process.ProcessName : title, 63),
                Visible = true,
            };
            notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) Restore(hwnd);
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Restore", null, (_, _) => Restore(hwnd));
            menu.Items.Add("Close window", null, (_, _) => CloseWindow(hwnd));
            notifyIcon.ContextMenuStrip = menu;
        }

        _trayed[hwnd] = new TrayedWindow { Hwnd = hwnd, Icon = notifyIcon, Process = process };

        // Remove the tray icon if the process dies while hidden.
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => RemoveIcon(hwnd));
        }
        catch
        {
            // Can't subscribe (access denied) — icon is still cleaned up on restore/exit.
        }
    }

    /// <summary>Minimize-then-hide (RBTray's technique): the window restores in its previous
    /// state instead of flashing full screen, and apps rarely force-show a minimized window.</summary>
    private static void HideWindow(IntPtr hwnd)
    {
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
    }

    /// <summary>
    /// Called when a window we trayed becomes visible again. Apps (especially Electron ones)
    /// often re-show their window several times while starting up — during the app's own
    /// startup window we simply hide it again. Once the process is past startup, any re-show
    /// is treated as the user intentionally bringing the app back (e.g. via the app's own
    /// tray icon): drop our tray icon, leave the window alone, and never auto-hide it again.
    /// </summary>
    public void HandleReShown(IntPtr hwnd, int graceSeconds)
    {
        if (!_trayed.TryGetValue(hwnd, out var trayed)) return;

        // Anchor the grace period to when the APP started, not when we hid it — a window
        // hidden by the startup sweep long after its process launched must come back the
        // moment the user asks for it.
        bool withinStartupGrace;
        try
        {
            withinStartupGrace = trayed.Process != null &&
                (DateTime.Now - trayed.Process.StartTime).TotalSeconds <= Math.Max(1, graceSeconds);
        }
        catch
        {
            // Process start time unreadable — allow only a short re-hide window after traying.
            withinStartupGrace = (DateTime.Now - trayed.TrayedAt).TotalSeconds <= 5;
        }

        if (withinStartupGrace && trayed.ReHideCount < MaxReHides)
        {
            trayed.ReHideCount++;
            HideWindow(hwnd);
            if (!NativeMethods.IsWindowVisible(hwnd)) return;
        }

        // Intentional re-show (or the app won't give up) — remove the stale icon and
        // remember that this window belongs to the user now.
        RemoveIcon(hwnd);
        _restoredByUser.Add(hwnd);
    }

    public void Restore(IntPtr hwnd)
    {
        RemoveIcon(hwnd);
        if (!NativeMethods.IsWindow(hwnd)) return;

        _restoredByUser.Add(hwnd);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Immediately tray every qualifying window of the given executable. Returns how many were trayed.
    /// With <paramref name="respectUserRestore"/> (used by the startup sweep), windows the user
    /// deliberately restored are left alone; the manual "Minimize to tray now" button overrides that.
    /// </summary>
    public int MinimizeAllWindowsOf(string exeName, bool suppressIcon = false, bool respectUserRestore = false)
    {
        int count = 0;
        foreach (var hwnd in NativeMethods.GetTopLevelWindows())
        {
            if (IsTrayed(hwnd)) continue;
            if (respectUserRestore && WasRestoredByUser(hwnd)) continue;
            if (!NativeMethods.IsWindowVisible(hwnd)) continue;
            if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) continue;
            if (NativeMethods.GetWindowTextLength(hwnd) == 0) continue;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) continue;

            Process process;
            try
            {
                process = Process.GetProcessById((int)pid);
            }
            catch
            {
                continue;
            }

            if (!string.Equals(process.ProcessName + ".exe", exeName, StringComparison.OrdinalIgnoreCase)) continue;

            MinimizeToTray(hwnd, process, suppressIcon);
            if (IsTrayed(hwnd)) count++;
        }
        return count;
    }

    public void RestoreAll()
    {
        foreach (var hwnd in _trayed.Keys.ToList())
        {
            Restore(hwnd);
        }
    }

    private void CloseWindow(IntPtr hwnd)
    {
        RemoveIcon(hwnd);
        if (NativeMethods.IsWindow(hwnd))
        {
            // Show it first so apps that prompt on close ("save changes?") aren't stuck invisible.
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void RemoveIcon(IntPtr hwnd)
    {
        if (_trayed.Remove(hwnd, out var trayed) && trayed.Icon != null)
        {
            trayed.Icon.Visible = false;
            trayed.Icon.Dispose();
        }
    }

    private static Icon GetWindowIcon(IntPtr hwnd, Process process)
    {
        // Try the window's own icon first (HICONs are shared USER objects, valid cross-process).
        foreach (var param in new[] { NativeMethods.ICON_SMALL2, NativeMethods.ICON_SMALL, NativeMethods.ICON_BIG })
        {
            IntPtr h = NativeMethods.SendMessage(hwnd, NativeMethods.WM_GETICON, param, IntPtr.Zero);
            if (h != IntPtr.Zero) return (Icon)Icon.FromHandle(h).Clone();
        }
        foreach (var index in new[] { NativeMethods.GCLP_HICONSM, NativeMethods.GCLP_HICON })
        {
            IntPtr h = NativeMethods.GetClassLongPtr(hwnd, index);
            if (h != IntPtr.Zero) return (Icon)Icon.FromHandle(h).Clone();
        }
        try
        {
            string? exePath = process.MainModule?.FileName;
            if (exePath != null)
            {
                var extracted = Icon.ExtractAssociatedIcon(exePath);
                if (extracted != null) return extracted;
            }
        }
        catch
        {
            // MainModule can throw for elevated processes.
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        RestoreAll();
        GC.SuppressFinalize(this);
    }
}
