using System.Diagnostics;
using System.Windows.Threading;
using TrayStart.Interop;

namespace TrayStart.Services;

/// <summary>
/// Ctrl+click a window's minimize or close caption button to send it to the tray and
/// auto-add its app to the watch list (RBTray-style gesture, via a low-level mouse hook).
///
/// Hot-path cost is negligible: mouse moves fall through on the first comparison; only
/// left-clicks with Ctrl held do any work. The hit-test uses SendMessageTimeout with
/// SMTO_ABORTIFHUNG so a hung app can't stall the mouse.
///
/// Limitation: apps that custom-draw their title bar without answering WM_NCHITTEST for
/// their buttons (some frameless Electron apps) can't be detected — nothing happens there.
/// </summary>
public sealed class CaptionClickHook : IDisposable
{
    private readonly App _app;
    private readonly Dispatcher _dispatcher;

    // Strong reference so the GC never collects the hook callback.
    private NativeMethods.LowLevelMouseProc? _proc;
    private IntPtr _hook;
    private bool _swallowNextUp;

    public CaptionClickHook(App app)
    {
        _app = app;
        _dispatcher = app.Dispatcher;
    }

    public bool IsActive => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        Log.Write($"ctrl+click gesture hook {(IsActive ? "installed" : "FAILED to install")}");
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        Log.Write("ctrl+click gesture hook removed");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == NativeMethods.WM_LBUTTONUP && _swallowNextUp)
            {
                _swallowNextUp = false;
                return 1; // complete the swallowed click pair
            }

            if (msg == NativeMethods.WM_LBUTTONDOWN &&
                (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0)
            {
                var data = System.Runtime.InteropServices.Marshal
                    .PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                IntPtr target = HitTestCaptionButton(data.pt);
                if (target != IntPtr.Zero)
                {
                    _swallowNextUp = true;
                    _dispatcher.BeginInvoke(() => HandleGesture(target));
                    return 1; // swallow the click so the app doesn't close/minimize
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Returns the root window if the point is on its minimize or close button.</summary>
    private static IntPtr HitTestCaptionButton(NativeMethods.POINT pt)
    {
        IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero) return IntPtr.Zero;

        NativeMethods.GetWindowThreadProcessId(root, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return IntPtr.Zero;

        IntPtr lparam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
        IntPtr result = NativeMethods.SendMessageTimeout(root, NativeMethods.WM_NCHITTEST,
            IntPtr.Zero, lparam, NativeMethods.SMTO_ABORTIFHUNG, 150, out IntPtr hit);
        if (result == IntPtr.Zero) return IntPtr.Zero; // timed out / hung app — let the click through

        long hitCode = hit.ToInt64();
        return hitCode is NativeMethods.HTCLOSE or NativeMethods.HTMINBUTTON ? root : IntPtr.Zero;
    }

    private void HandleGesture(IntPtr hwnd)
    {
        try
        {
            if (!NativeMethods.IsWindow(hwnd)) return;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            var process = Process.GetProcessById((int)pid);
            string exeName = process.ProcessName + ".exe";

            Log.Write($"gesture: ctrl+click on {exeName} hwnd={hwnd} \"{NativeMethods.GetWindowTitle(hwnd)}\"");

            var watched = _app.AddOrGetWatchedApp(exeName, GetDisplayName(process));
            _app.Minimizer.MinimizeToTray(hwnd, process, suppressIcon: watched.HasOwnTray, userInitiated: true);
        }
        catch (Exception ex)
        {
            Log.Write($"gesture: failed — {ex.Message}");
        }
    }

    private static string GetDisplayName(Process process)
    {
        try
        {
            var info = process.MainModule?.FileVersionInfo;
            string? name = string.IsNullOrWhiteSpace(info?.ProductName) ? info?.FileDescription : info.ProductName;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch
        {
            // Elevated process — fall through.
        }
        return process.ProcessName;
    }

    public void Dispose()
    {
        Stop();
    }
}
