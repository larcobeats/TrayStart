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
        // Hidden by an explicit user action (gesture / "Minimize now" button): the user's
        // recent input must not be misread as them restoring the window moments later.
        public bool UserInitiatedHide { get; init; }
    }

    // Give up re-hiding a window that keeps force-showing itself after this many attempts.
    private const int MaxReHides = 8;

    private readonly Dictionary<IntPtr, TrayedWindow> _trayed = new();

    // Windows the user explicitly restored — never auto-hide these again.
    private readonly HashSet<IntPtr> _restoredByUser = new();

    public bool IsTrayed(IntPtr hwnd) => _trayed.ContainsKey(hwnd);

    public bool WasRestoredByUser(IntPtr hwnd) => _restoredByUser.Contains(hwnd);

    /// <param name="suppressIcon">The app has its own tray icon; hide the window but don't add a duplicate icon.</param>
    /// <param name="userInitiated">The hide was an explicit user action (gesture or button).</param>
    public void MinimizeToTray(IntPtr hwnd, Process process, bool suppressIcon = false, bool userInitiated = false)
    {
        if (IsTrayed(hwnd)) return;
        _restoredByUser.Remove(hwnd); // an explicit tray request overrides an earlier restore

        string title = NativeMethods.GetWindowTitle(hwnd);
        Icon? icon = suppressIcon ? null : GetWindowIcon(hwnd, process);

        HideWindow(hwnd);
        if (NativeMethods.IsWindowVisible(hwnd))
        {
            // Hide was blocked (e.g. elevated window and we're not) — don't orphan a tray icon.
            Log.Write($"hide BLOCKED for {process.ProcessName} hwnd={hwnd} \"{title}\" (still visible — elevated?)");
            icon?.Dispose();
            return;
        }
        Log.Write($"hid {process.ProcessName} hwnd={hwnd} \"{title}\" (ownIcon={suppressIcon})");

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

        _trayed[hwnd] = new TrayedWindow
        {
            Hwnd = hwnd,
            Icon = notifyIcon,
            Process = process,
            UserInitiatedHide = userInitiated,
        };

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
    /// Called when a window we trayed becomes visible again. Deciding between "the app
    /// force-reshowed itself" (re-hide it) and "the user brought it back" (release it,
    /// permanently):
    ///  - Recent user input (click/keypress within ~2s) always means the user — release.
    ///  - Otherwise, re-hide while the app is starting up, right after we hid it, or any
    ///    time during boot mode — self-reshows in those windows are never user intent.
    ///  - Anything else is the user (e.g. app's own tray icon) — release.
    /// </summary>
    public void HandleReShown(IntPtr hwnd, int graceSeconds)
    {
        if (!_trayed.TryGetValue(hwnd, out var trayed)) return;

        int grace = Math.Max(1, graceSeconds);
        double processAge = double.MaxValue;
        try
        {
            if (trayed.Process != null)
            {
                processAge = (DateTime.Now - trayed.Process.StartTime).TotalSeconds;
            }
        }
        catch
        {
            // Unreadable (elevated) — rely on the other signals.
        }
        double trayedAge = (DateTime.Now - trayed.TrayedAt).TotalSeconds;
        double inputAge = NativeMethods.SecondsSinceLastInput();

        // Right after the user explicitly hid this window, their input is the CAUSE of the
        // hide — an immediate re-show is the app fighting back, not the user changing course.
        bool userLikelyDidIt = inputAge <= 2.0 && !(trayed.UserInitiatedHide && trayedAge <= grace);
        bool selfReshowWindow = processAge <= grace || trayedAge <= grace || WindowWatcher.InBootMode;

        if (!userLikelyDidIt && selfReshowWindow && trayed.ReHideCount < MaxReHides)
        {
            trayed.ReHideCount++;
            Log.Write($"reshown hwnd={hwnd}: re-hide #{trayed.ReHideCount} " +
                $"(procAge={processAge:F0}s trayedAge={trayedAge:F0}s input={inputAge:F1}s boot={WindowWatcher.InBootMode})");
            HideWindow(hwnd);
            if (!NativeMethods.IsWindowVisible(hwnd)) return;
            Log.Write($"reshown hwnd={hwnd}: re-hide FAILED, window still visible");
        }

        // User restore (or the app won't give up) — remove our icon and remember that
        // this window belongs to the user now.
        string reason = userLikelyDidIt ? "user input" :
            !selfReshowWindow ? "outside re-hide windows" : "re-hide cap reached";
        Log.Write($"reshown hwnd={hwnd}: RELEASED to user ({reason}; " +
            $"procAge={processAge:F0}s trayedAge={trayedAge:F0}s input={inputAge:F1}s boot={WindowWatcher.InBootMode})");
        RemoveIcon(hwnd);
        _restoredByUser.Add(hwnd);
    }

    public void Restore(IntPtr hwnd)
    {
        RemoveIcon(hwnd);
        if (!NativeMethods.IsWindow(hwnd)) return;

        Log.Write($"restore hwnd={hwnd} (user, via TrayStart)");
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
    public int MinimizeAllWindowsOf(string exeName, bool suppressIcon = false, bool respectUserRestore = false,
        bool userInitiated = false)
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

            MinimizeToTray(hwnd, process, suppressIcon, userInitiated);
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
