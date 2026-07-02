using System.IO;
using Microsoft.Win32;

namespace TrayStart.Services;

/// <summary>
/// Detects whether an app brings its own tray icon, using Windows 11's per-user tray icon
/// store (HKCU\Control Panel\NotifyIconSettings — one subkey per icon, with ExecutablePath).
/// Note: entries are historical ("has this exe ever shown a tray icon"), which is the right
/// question here — it means the app has a native tray feature and ours would be a duplicate.
/// </summary>
public static class TrayIconRegistry
{
    private const string SettingsKeyPath = @"Control Panel\NotifyIconSettings";

    public static bool ExeHasOwnTrayIcon(string exeName)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            if (root == null) return false;

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                if (subKey?.GetValue("ExecutablePath") is string path &&
                    string.Equals(Path.GetFileName(path), exeName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Registry unavailable — assume no native icon so the window stays reachable.
        }
        return false;
    }
}
