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
        public required NotifyIcon Icon { get; init; }
        public Process? Process { get; init; }
    }

    private readonly Dictionary<IntPtr, TrayedWindow> _trayed = new();

    // Windows the user explicitly restored — never auto-hide these again.
    private readonly HashSet<IntPtr> _restoredByUser = new();

    public bool IsTrayed(IntPtr hwnd) => _trayed.ContainsKey(hwnd);

    public bool WasRestoredByUser(IntPtr hwnd) => _restoredByUser.Contains(hwnd);

    public void MinimizeToTray(IntPtr hwnd, Process process)
    {
        string title = NativeMethods.GetWindowTitle(hwnd);
        Icon icon = GetWindowIcon(hwnd, process);

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        if (NativeMethods.IsWindowVisible(hwnd))
        {
            // Hide was blocked (e.g. elevated window and we're not) — don't orphan a tray icon.
            icon.Dispose();
            return;
        }

        var notifyIcon = new NotifyIcon
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

    public void Restore(IntPtr hwnd)
    {
        RemoveIcon(hwnd);
        if (!NativeMethods.IsWindow(hwnd)) return;

        _restoredByUser.Add(hwnd);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }
        NativeMethods.SetForegroundWindow(hwnd);
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
        if (_trayed.Remove(hwnd, out var trayed))
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
